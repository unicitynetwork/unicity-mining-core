using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Alpha
{
    [CoinFamily(CoinFamily.Bitcoin)]
    public class AlphaPayoutHandler : BitcoinPayoutHandler
    {
        public AlphaPayoutHandler(
            IComponentContext ctx,
            IConnectionFactory cf,
            IMapper mapper,
            IShareRepository shareRepo,
            IBlockRepository blockRepo,
            IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
        {
        }

        protected override string LogCategory => "Alpha Payout Handler";

        /// <summary>
        /// Override ClassifyBlocksAsync to use blockchain-level commands instead of wallet commands
        /// This allows block classification to work even when the wallet is on a different machine
        /// </summary>
        public override async Task<Persistence.Model.Block[]> ClassifyBlocksAsync(IMiningPool pool, Persistence.Model.Block[] blocks, CancellationToken ct)
        {
            Contract.RequiresNonNull(poolConfig);
            Contract.RequiresNonNull(blocks);

            if(blocks.Length == 0)
                return blocks;

            var coin = poolConfig.Template.As<CoinTemplate>();
            var pageSize = 100;
            var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
            var result = new List<Persistence.Model.Block>();

            // Get current blockchain info for confirmation counting
            var infoResponse = await rpcClient.ExecuteAsync<Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockchainInfo>(logger, BitcoinCommands.GetBlockchainInfo, ct);
            if(infoResponse.Error != null)
            {
                logger.Warn(() => $"[{LogCategory}] Error getting blockchain info: {infoResponse.Error.Message}");
                return blocks;
            }

            var currentBlockHeight = infoResponse.Response.Blocks;

            // Determine minimum confirmations required
            var bitcoinTemplate = coin as BitcoinTemplate;
            var minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? (bitcoinTemplate?.CoinbaseMinConfimations ?? BitcoinConstants.CoinbaseMinConfimations);
            
            for(var i = 0; i < pageCount; i++)
            {
                // Get a page full of blocks
                var page = blocks
                    .Skip(i * pageSize)
                    .Take(pageSize)
                    .ToArray();

                for(var j = 0; j < page.Length; j++)
                {
                    var block = page[j];
                    
                    // Skip blocks that don't have hashes (shouldn't happen, but just in case)
                    if(string.IsNullOrEmpty(block.Hash))
                    {
                        logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} has no hash");
                        continue;
                    }

                    // Get block information using the hash stored in our database
                    // Use verbose=1 to get transaction IDs (we don't need full tx details)
                    var blockResponse = await rpcClient.ExecuteAsync<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>(logger, BitcoinCommands.GetBlock, ct, new object[] { block.Hash, 1 });
                    
                    // If the daemon doesn't recognize the block hash, it's orphaned
                    if(blockResponse.Error != null)
                    {
                        if(blockResponse.Error.Code == (int)BitcoinRPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
                        {
                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                            result.Add(block);
                            
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned - block hash not found in blockchain");
                            
                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        }
                        else
                        {
                            logger.Warn(() => $"[{LogCategory}] Error retrieving block {block.BlockHeight}: {blockResponse.Error.Message}");
                        }
                        
                        continue;
                    }
                    
                    var blockInfo = blockResponse.Response;
                    
                    // If the block has negative confirmations, it's orphaned
                    if(blockInfo.Confirmations < 0)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);
                        
                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned - negative confirmations");
                        
                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        continue;
                    }
                    
                    // Check confirmation progress
                    var confirmations = blockInfo.Confirmations;
                    var confirmationProgress = Math.Min(1.0d, (double) confirmations / minConfirmations);
                    
                    // Update progress
                    block.ConfirmationProgress = confirmationProgress;
                    
                    // Keep the original reward value from when the block was found
                    // We can't directly get the reward from getblock as it doesn't provide that info
                    result.Add(block);
                    
                    messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);
                    
                    // Check if the block is confirmed (matured)
                    if(confirmations >= minConfirmations)
                    {
                        block.Status = BlockStatus.Confirmed;
                        block.ConfirmationProgress = 1;
                        
                        // Set reward for Alpha blocks (10 coins per block)
                        block.Reward = 10.0m;
                        
                        logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");
                        
                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                }
            }
            
            return result.ToArray();
        }
        
        /// <summary>
        /// Override PayoutAsync to queue payments for external processing
        /// instead of sending them directly via the daemon.
        /// </summary>
        public override async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
        {
            Contract.RequiresNonNull(balances);

            // build args
            var amounts = balances
                .Where(x => x.Amount > 0)
                .ToDictionary(x => x.Address, x => Math.Round(x.Amount, 4));

            if(amounts.Count == 0)
                return;

            logger.Info(() => $"[{LogCategory}] Queuing {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses for external processing");

            // Create payment records with empty transaction confirmation data
            // These will be filled in by the external payment system
            try
            {
                var coin = poolConfig.Template.As<CoinTemplate>().Symbol;

                var payments = balances
                    .Select(x => new Payment
                    {
                        PoolId = poolConfig.Id,
                        Coin = coin,
                        Address = x.Address,
                        Amount = x.Amount,
                        Created = clock.Now,
                        TransactionConfirmationData = string.Empty // Will be populated by external system
                    })
                    .ToArray();

                // Insert payment records
                await cf.RunTx(async (con, tx) =>
                {
                    foreach(var payment in payments)
                        await paymentRepo.InsertAsync(con, tx, payment);

                    // Reset balances
                    foreach(var balance in balances)
                    {
                        // Reset balance via AddAmountAsync with negative amount
                        await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, balance.Address, -balance.Amount, "Balance reset after queueing payment");
                    }
                });

                NotifyPayoutSuccess(poolConfig.Id, balances, new[] { "queued" }, null);

                logger.Info(() => $"[{LogCategory}] Successfully queued {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses for external processing");
            }
            catch(Exception ex)
            {
                logger.Error(ex, () => $"[{LogCategory}] Failed to queue payments");
                NotifyPayoutFailure(poolConfig.Id, balances, $"Failed to queue payments: {ex.Message}", null);
            }
        }
    }
}
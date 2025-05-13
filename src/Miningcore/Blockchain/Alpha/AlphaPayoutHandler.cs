using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
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
using System.Linq;
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
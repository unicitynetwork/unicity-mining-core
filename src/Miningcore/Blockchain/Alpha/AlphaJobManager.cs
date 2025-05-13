using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Rpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using NBitcoin;
using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Alpha
{
    public class AlphaJobManager : BitcoinJobManager
    {
        private new static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        public AlphaJobManager(
            IComponentContext ctx,
            IMasterClock clock, 
            IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider) :
            base(ctx, clock, messageBus, extraNonceProvider)
        {
        }
        
        

        // Custom method to decode Alpha Bech32 addresses
        protected override IDestination AddressToDestination(string address, BitcoinAddressType? addressType)
        {
            if(address.StartsWith("alpha1") && addressType == BitcoinAddressType.BechSegwit)
            {
                Console.WriteLine($"Processing Alpha Bech32 address: {address}");
                
                try
                {
                    // Create a custom Bech32 encoder with the "alpha" HRP
                    var alphaEncoder = new NBitcoin.DataEncoders.Bech32Encoder(System.Text.Encoding.ASCII.GetBytes("alpha"));
                    
                    // Decode the address using the Alpha encoder
                    // This simulates what Network.GetBech32Encoder would normally do but with the correct HRP
                    byte witnessVersion;
                    var data = alphaEncoder.Decode(address, out witnessVersion);
                    
                    // Create a WitKeyId from the decoded data
                    var result = new NBitcoin.WitKeyId(data);
                    
                    Console.WriteLine($"Successfully decoded Alpha Bech32 address");
                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error decoding Alpha Bech32 address: {ex.Message}");
                    
                    // Instead of using a dummy KeyId fallback, we should throw an exception
                    // This will cause the validation to fail properly
                    throw new ArgumentException($"Invalid Alpha Bech32 address: {address}", ex);
                }
            }
            
            // For other address types, use the base implementation
            return base.AddressToDestination(address, addressType);
        }


        protected override object GetJobParamsForStratum(bool isNew)
        {
            var job = currentJob as AlphaJob;
            
            // Add logging to help debug when job is null and where it's being called from
            if (job == null)
            {
                logger.Error(() => $"GetJobParamsForStratum called with null job. isNew={isNew}, hasInitialBlockTemplate={hasInitialBlockTemplate}");
                // Log stack trace to help understand where this is being called from
                logger.Error(() => $"Stack trace: {Environment.StackTrace}");
            }
            else
            {
                logger.Debug(() => $"GetJobParamsForStratum: job.JobId={job.JobId}, isNew={isNew}");
            }
            
            return job?.GetJobParams(isNew);
        }

        protected AlphaJob CreateJob()
        {
            return new AlphaJob();
        }
        
        protected override object[] GetBlockTemplateParams()
        {
            // Alpha requires explicit segwit rule
            return new object[]
            {
                new
                {
                    rules = new[] {"segwit"},
                }
            };
        }

        protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
        {
            try
            {
                if (forceUpdate)
                    lastJobRebroadcast = clock.Now;
                
                var response = string.IsNullOrEmpty(json) ?
                    await GetBlockTemplateAsync(ct) :
                    GetBlockTemplateFromJson(json);
                
                // may happen if daemon is currently not connected to peers
                if(response.Error != null)
                {
                    logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                    return (false, forceUpdate);
                }

                
                var blockTemplate = response.Response;
                var job = currentJob as AlphaJob;

                // Extract required rx_epoch_duration from block template
                if (blockTemplate?.Extra == null || !blockTemplate.Extra.TryGetValue("rx_epoch_duration", out var epochDurationObj))
                {
                    logger.Error(() => $"Alpha daemon did not provide required rx_epoch_duration in block template");
                    return (false, forceUpdate);
                }
                
                // Try to parse rx_epoch_duration in various formats
                uint epochDuration;  //TODO
                try
                {
                    if (epochDurationObj is JToken jToken)
                        epochDuration = jToken.Value<uint>();
                    else if (epochDurationObj is uint u)
                        epochDuration = u;
                    else if (epochDurationObj != null && uint.TryParse(epochDurationObj.ToString(), out var parsed))
                        epochDuration = parsed;
                    else
                    {
                        logger.Error(() => $"Invalid rx_epoch_duration format in block template: {epochDurationObj}");
                        return (false, forceUpdate);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(() => $"Error parsing rx_epoch_duration from daemon: {ex.Message}");
                    return (false, forceUpdate);
                }
                

                var isNew = job == null ||
                    (blockTemplate != null &&
                        (job.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                            blockTemplate.Height > job.BlockTemplate?.Height));

                if (isNew)
                    messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

                if (isNew || forceUpdate)
                {
                    job = CreateJob();

                    // Use custom initialization for Alpha job with the extracted rx_epoch_duration
                    job.InitAlpha(blockTemplate, NextJobId(),
                        poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network,
                        false, // Alpha uses PoW not PoS
                        ShareMultiplier, coin.CoinbaseHasherValue, 
                        coin.BlockHasherValue?.GetType() == typeof(Sha256D) 
                            ? coin.BlockHasherValue 
                            : throw new Exception("Alpha requires SHA256D for final block hash"), 
                        epochDuration); // Use the extracted value

                    lock (jobLock)
                    {
                        validJobs.Insert(0, job);

                        // trim active jobs
                        while (validJobs.Count > maxActiveJobs)
                            validJobs.RemoveAt(validJobs.Count - 1);
                    }

                    if (isNew)
                    {
                        if (via != null)
                            logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                        else
                            logger.Info(() => $"Detected new block {blockTemplate.Height}");

                        // update stats
                        BlockchainStats.LastNetworkBlockTime = clock.Now;
                        BlockchainStats.BlockHeight = blockTemplate.Height;
                        BlockchainStats.NetworkDifficulty = job.Difficulty;
                        BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                        BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                    }

                    else
                    {
                        if (via != null)
                            logger.Debug(() => $"Template update {blockTemplate?.Height} [{via}]");
                        else
                            logger.Debug(() => $"Template update {blockTemplate?.Height}");
                    }

                    currentJob = job;
                }

                return (isNew, forceUpdate);
            }

            catch (OperationCanceledException)
            {
                // ignored
            }

            catch (Exception ex)
            {
                logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
            }

            return (false, forceUpdate);
        }
    }
}
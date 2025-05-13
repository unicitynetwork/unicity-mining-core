using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence.Repositories;
using Miningcore.Persistence;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using NLog;
using static Miningcore.Util.ActionUtils;
using Miningcore.Util;

namespace Miningcore.Blockchain.Alpha;

[CoinFamily(CoinFamily.Bitcoin)]
public class AlphaPool : BitcoinPool
{
    private readonly AlphaPayoutHandler payoutHandler;

    public AlphaPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService,
        AlphaPayoutHandler payoutHandler) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
        this.payoutHandler = payoutHandler;
    }

    // NOTE: We inherit the base class's `manager` field, which is of type BitcoinJobManager.
    // However, the instance assigned in SetupJobManager MUST be AlphaJobManager for Alpha-specific logic.
    // This works because AlphaJobManager inherits from BitcoinJobManager.
    // The `currentJobParams` will be populated by AlphaJobManager with the correct format.

    protected override async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();
        var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
        var password = requestParams?.Length > 1 ? requestParams[1] : null;
        var passParts = password?.Split(PasswordControlVarsSeparator);

        // extract worker/miner
        var split = workerValue?.Split('.');
        var minerName = split?.FirstOrDefault()?.Trim();
        var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

        // assumes that minerName is an address
        context.IsAuthorized = await manager.ValidateAddressAsync(minerName, ct);
        context.Miner = minerName;
        context.Worker = workerName;

        if(context.IsAuthorized)
        {
            // respond
            await connection.RespondAsync(context.IsAuthorized, request.Id);

            // log association
            logger.Info(() => $"[{connection.ConnectionId}] Authorized worker {workerValue}");

            // extract control vars from password
            var staticDiff = GetStaticDiffFromPassparts(passParts);

            // Static diff
            if(staticDiff.HasValue &&
               (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                   context.VarDiff == null && staticDiff.Value > context.Difficulty))
            {
                context.VarDiff = null; // disable vardiff
                context.SetDifficulty(staticDiff.Value);

                logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");

                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
            }
        }
        else
        {
            await connection.RespondErrorAsync(StratumError.UnauthorizedWorker, "Authorization failed", request.Id, context.IsAuthorized);

            if(clusterConfig?.Banning?.BanOnLoginFailure is null or true)
            {
                // issue short-time ban if unauthorized to prevent DDos on daemon (validateaddress RPC)
                logger.Info(() => $"[{connection.ConnectionId}] Banning unauthorized worker {minerName} for {loginFailureBanTimeout.TotalSeconds} sec");

                banManager.Ban(connection.RemoteEndpoint.Address, loginFailureBanTimeout);

                Disconnect(connection);
            }
        }
    }
    
    // Override OnSubscribeAsync method to handle Alpha specific RandomX subscription
    protected override async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();

        var data = new object[]
        {
            new object[]
            {
                new object[] { BitcoinStratumMethods.SetDifficulty, connection.ConnectionId },
                new object[] { BitcoinStratumMethods.MiningNotify, connection.ConnectionId }
            }
        }
        .Concat(manager.GetSubscriberData(connection))
        .ToArray();

        await connection.RespondAsync(data, request.Id);

        // setup worker context
        context.IsSubscribed = true;
        context.UserAgent = requestParams.FirstOrDefault()?.Trim();

        // send initial update
        await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
        await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
    }


    // This override is necessary despite being almost identical to the base class method.
    // The key difference is resolving AlphaJobManager specifically.
    // Attempting to generalize this via a virtual ResolveJobManager() in the base class
    // led to type compatibility issues because BitcoinPool relies on specific methods/properties
    // of BitcoinJobManager not present in a generic JobManagerBase.
    
     protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<AlphaJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider), new BitcoinExtraNonceProvider(poolConfig.Id, clusterConfig.InstanceId)));

        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(()=> OnNewJobAsync(job),
                        ex=> logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex =>
                {
                    logger.Debug(ex, nameof(OnNewJobAsync));
                }));

            // start with initial blocktemplate
            await manager.Jobs.Take(1).ToTask(ct);
        }

        else
        {
            // keep updating NetworkStats
            disposables.Add(manager.Jobs.Subscribe());
        }
    }
    
    // AlphaPool uses AlphaPayoutHandler which has been registered in the DI container.
    // The PayoutManager automatically selects it based on CoinFamily.
}

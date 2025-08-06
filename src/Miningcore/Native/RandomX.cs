using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using NLog;

// ReSharper disable UnusedMember.Global
// ReSharper disable InconsistentNaming

namespace Miningcore.Native;

public static unsafe class RandomX
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    internal static IMessageBus messageBus;

    #region VM managment

    internal static readonly Dictionary<string, Dictionary<string, Tuple<GenContext, BlockingCollection<RxVm>>>> realms = new();
    private static readonly byte[] empty = new byte[32];

    #endregion // VM managment

    [Flags]
    public enum randomx_flags
    {
        RANDOMX_FLAG_DEFAULT = 0,
        RANDOMX_FLAG_LARGE_PAGES = 1,
        RANDOMX_FLAG_HARD_AES = 2,
        RANDOMX_FLAG_FULL_MEM = 4,
        RANDOMX_FLAG_JIT = 8,
        RANDOMX_FLAG_SECURE = 16,
        RANDOMX_FLAG_ARGON2_SSSE3 = 32,
        RANDOMX_FLAG_ARGON2_AVX2 = 64,
        RANDOMX_FLAG_ARGON2 = 96
    };

    [DllImport("librandomx", EntryPoint = "randomx_get_flags", CallingConvention = CallingConvention.Cdecl)]
    private static extern randomx_flags randomx_get_flags();

    [DllImport("librandomx", EntryPoint = "randomx_alloc_cache", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr alloc_cache(randomx_flags flags);

    [DllImport("librandomx", EntryPoint = "randomx_init_cache", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr init_cache(IntPtr cache, IntPtr key, int keysize);

    [DllImport("librandomx", EntryPoint = "randomx_release_cache", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr release_cache(IntPtr cache);

    [DllImport("librandomx", EntryPoint = "randomx_alloc_dataset", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr alloc_dataset(randomx_flags flags);

    [DllImport("librandomx", EntryPoint = "randomx_dataset_item_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong dataset_item_count();

    [DllImport("librandomx", EntryPoint = "randomx_init_dataset", CallingConvention = CallingConvention.Cdecl)]
    private static extern void init_dataset(IntPtr dataset, IntPtr cache, ulong startItem, ulong itemCount);

    [DllImport("librandomx", EntryPoint = "randomx_get_dataset_memory", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr get_dataset_memory(IntPtr dataset);

    [DllImport("librandomx", EntryPoint = "randomx_release_dataset", CallingConvention = CallingConvention.Cdecl)]
    private static extern void release_dataset(IntPtr dataset);

    [DllImport("librandomx", EntryPoint = "randomx_create_vm", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr create_vm(randomx_flags flags, IntPtr cache, IntPtr dataset);

    [DllImport("librandomx", EntryPoint = "randomx_vm_set_cache", CallingConvention = CallingConvention.Cdecl)]
    private static extern void vm_set_cache(IntPtr machine, IntPtr cache);

    [DllImport("librandomx", EntryPoint = "randomx_vm_set_dataset", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vm_set_dataset(IntPtr machine, IntPtr dataset);

    [DllImport("librandomx", EntryPoint = "randomx_destroy_vm", CallingConvention = CallingConvention.Cdecl)]
    private static extern void destroy_vm(IntPtr machine);

    [DllImport("librandomx", EntryPoint = "randomx_calculate_hash", CallingConvention = CallingConvention.Cdecl)]
    private static extern void calculate_hash(IntPtr machine, byte* input, int inputSize, byte* output);
    
    [DllImport("librandomx", EntryPoint = "randomx_calculate_commitment", CallingConvention = CallingConvention.Cdecl)]
    private static extern void calculate_commitment(byte* input, int inputSize, byte* hash_in, byte* com_out);

    public class GenContext
    {
        public DateTime LastAccess { get; set; } = DateTime.Now;
        public int VmCount { get; init; }
    }

    public class RxDataSet : IDisposable
    {
        private IntPtr dataset = IntPtr.Zero;

        public void Dispose()
        {
            if(dataset != IntPtr.Zero)
            {
                release_dataset(dataset);
                dataset = IntPtr.Zero;
            }
        }

        public IntPtr Init(randomx_flags flags, IntPtr cache)
        {
            dataset = alloc_dataset(flags);

            var itemCount = dataset_item_count();
            init_dataset(dataset, cache, 0, itemCount);

            return dataset;
        }
    }

    public class RxVm : IDisposable
    {
        private IntPtr cache = IntPtr.Zero;
        private IntPtr vm = IntPtr.Zero;
        private RxDataSet ds;

        public void Dispose()
        {
            if(vm != IntPtr.Zero)
            {
                destroy_vm(vm);
                vm = IntPtr.Zero;
            }

            ds?.Dispose();

            if(cache != IntPtr.Zero)
            {
                release_cache(cache);
                cache = IntPtr.Zero;
            }
        }

        public void Init(ReadOnlySpan<byte> key, randomx_flags flags)
        {
            var ds_ptr = IntPtr.Zero;

            // alloc cache
            cache = alloc_cache(flags);

            // init cache
            fixed(byte* key_ptr = key)
            {
                init_cache(cache, (IntPtr) key_ptr, key.Length);
            }

            // Enable fast-mode? (requires 2GB+ memory per VM)
            if((flags & randomx_flags.RANDOMX_FLAG_FULL_MEM) != 0)
            {
                ds = new RxDataSet();
                ds_ptr = ds.Init(flags, cache);

                // cache is no longer needed in fast-mode
                release_cache(cache);
                cache = IntPtr.Zero;
            }

            vm = create_vm(flags, cache, ds_ptr);
        }

        public void CalculateHash(ReadOnlySpan<byte> data, Span<byte> result)
        {
            fixed (byte* input = data)
            {
                fixed (byte* output = result)
                {
                    calculate_hash(vm, input, data.Length, output);
                }
            }
        }
    }

    public static void WithLock(Action action)
    {
        lock(realms)
        {
            action();
        }
    }

    public static void CreateSeed(string realm, string seedHex,
        randomx_flags? flagsOverride = null, randomx_flags? flagsAdd = null, int vmCount = 1)
    {
        logger.Debug(() => $"DEBUG-RandomX: CreateSeed called for realm={realm}, seedHex={seedHex}");
        
        lock(realms)
        {
            if(!realms.TryGetValue(realm, out var seeds))
            {
                logger.Debug(() => $"DEBUG-RandomX: Creating new realm dictionary for {realm}");
                seeds = new Dictionary<string, Tuple<GenContext, BlockingCollection<RxVm>>>();
                realms[realm] = seeds;
            }
            else
            {
                logger.Debug(() => $"DEBUG-RandomX: Found existing realm: {realm}");
            }

            if(!seeds.TryGetValue(seedHex, out var seed))
            {
                logger.Debug(() => $"DEBUG-RandomX: Seed {seedHex} not found, creating new VMs");
                
                var flags = flagsOverride ?? randomx_get_flags();
                if(flagsAdd.HasValue)
                    flags |= flagsAdd.Value;
                    
                logger.Debug(() => $"DEBUG-RandomX: Using flags: {flags}");

                if (vmCount == -1)
                    vmCount = Environment.ProcessorCount;
                    
                logger.Debug(() => $"DEBUG-RandomX: Creating {vmCount} VMs");

                seed = CreateSeed(realm, seedHex, flags, vmCount);
                seeds[seedHex] = seed;
                
                logger.Debug(() => $"DEBUG-RandomX: Seed {seedHex} created and stored for realm {realm}");
            }
            else
            {
                logger.Debug(() => $"DEBUG-RandomX: Seed {seedHex} already exists for realm {realm}");
            }
        }
    }

    private static Tuple<GenContext, BlockingCollection<RxVm>> CreateSeed(string realm, string seedHex, randomx_flags flags, int vmCount)
    {
        var vms = new BlockingCollection<RxVm>();

        var seed = new Tuple<GenContext, BlockingCollection<RxVm>>(new GenContext
        {
            VmCount = vmCount
        }, vms);

        void createVm(int index)
        {
            var start = DateTime.Now;
            logger.Info(() => $"Creating VM {realm}@{index + 1} [{flags}], hash {seedHex} ...");

            var vm = new RxVm();
            vm.Init(seedHex.HexToByteArray(), flags);

            vms.Add(vm);

            logger.Info(() => $"Created VM {realm}@{index + 1} in {DateTime.Now - start}");
        };

        Parallel.For(0, vmCount, createVm);

        return seed;
    }

    public static void DeleteSeed(string realm, string seedHex)
    {
        Tuple<GenContext, BlockingCollection<RxVm>> seed;

        lock(realms)
        {
            if(!realms.TryGetValue(realm, out var seeds))
                return;

            if(!seeds.Remove(seedHex, out seed))
                return;
        }

        // dispose all VMs
        var (ctx, col) = seed;
        var remaining = ctx.VmCount;

        while (remaining > 0)
        {
            var vm = col.Take();

            logger.Info($"Disposing VM {ctx.VmCount - remaining} for realm {realm} and key {seedHex}");
            vm.Dispose();

            remaining--;
        }
    }

    public static Tuple<GenContext, BlockingCollection<RxVm>> GetSeed(string realm, string seedHex)
    {
        lock(realms)
        {
            if(!realms.TryGetValue(realm, out var seeds))
                return null;

            if(!seeds.TryGetValue(seedHex, out var seed))
                return null;

            return seed;
        }
    }

    public static void CalculateHash(string realm, string seedHex, ReadOnlySpan<byte> data, Span<byte> result)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        var sw = Stopwatch.StartNew();
        var success = false;
        
        // For logging, we need to convert spans to arrays
        var dataBytes = data.ToArray();
        logger.Debug(() => $"DEBUG-RandomX: Starting CalculateHash for realm={realm}, seedHex={seedHex}, dataLength={dataBytes.Length}");
        var dataPrefix = dataBytes.Length >= 16 ? BitConverter.ToString(dataBytes, 0, 16).Replace("-", "") : BitConverter.ToString(dataBytes).Replace("-", "");
        logger.Debug(() => $"DEBUG-RandomX: First 16 bytes of data: {dataPrefix}");

        var (ctx, seedVms) = GetSeed(realm, seedHex);

        if(ctx != null)
        {
            logger.Debug(() => $"DEBUG-RandomX: Found seed for realm={realm}, seedHex={seedHex}");
            RxVm vm = null;

            try
            {
                // lease a VM
                vm = seedVms.Take();
                logger.Debug(() => $"DEBUG-RandomX: Acquired VM for hashing");

                vm.CalculateHash(data, result);
                
                // Copy to array for logging
                var resultBytes = result.ToArray();
                logger.Debug(() => $"DEBUG-RandomX: Computed hash: {resultBytes.ToHexString()}");

                ctx.LastAccess = DateTime.Now;
                success = true;

                messageBus?.SendTelemetry("RandomX", TelemetryCategory.Hash, sw.Elapsed, true);
            }
            catch(Exception ex)
            {
                logger.Debug(() => $"DEBUG-RandomX: Error calculating hash: {ex.Message}");
                logger.Debug(() => ex.StackTrace);
            }
            finally
            {
                // return it
                if(vm != null)
                {
                    seedVms.Add(vm);
                    logger.Debug(() => $"DEBUG-RandomX: Returned VM to pool");
                }
            }
        }
        else
        {
            logger.Debug(() => $"DEBUG-RandomX: NO SEED FOUND for realm={realm}, seedHex={seedHex}");
        }

        if(!success)
        {
            // clear result on failure
            empty.CopyTo(result);
            logger.Debug(() => $"DEBUG-RandomX: Returning empty result due to failure");
        }
        else
        {
            logger.Debug(() => $"DEBUG-RandomX: Successfully calculated hash in {sw.ElapsedMilliseconds}ms");
        }
    }
    
    /// <summary>
    /// Calculates the RandomX commitment hash for a given input and hash
    /// </summary>
    /// <param name="input">The input data (typically the original header bytes)</param>
    /// <param name="hash_in">The RandomX hash result</param>
    /// <param name="commitment_result">The output span where the commitment hash will be written</param>
    public static void CalculateCommitment(ReadOnlySpan<byte> input, ReadOnlySpan<byte> hash_in, Span<byte> commitment_result)
    {
        Contract.Requires<ArgumentException>(hash_in.Length >= 32, "Hash input must be at least 32 bytes");
        Contract.Requires<ArgumentException>(commitment_result.Length >= 32, "Commitment result buffer must be at least 32 bytes");
        
        var sw = Stopwatch.StartNew();
        
        try
        {
            fixed (byte* input_ptr = input)
            fixed (byte* hash_ptr = hash_in)
            fixed (byte* result_ptr = commitment_result)
            {
                calculate_commitment(input_ptr, input.Length, hash_ptr, result_ptr);
                
                messageBus?.SendTelemetry("RandomX", TelemetryCategory.Hash, sw.Elapsed, true);
            }
        }
        catch (Exception ex)
        {
            logger.Error(() => $"Error calculating RandomX commitment: {ex.Message}");
            // clear result on failure
            empty.CopyTo(commitment_result);
        }
    }
}

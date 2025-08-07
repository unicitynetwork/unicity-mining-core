using System.Buffers.Binary; // Added for explicit Endianness control
using System.Globalization;
using System.Numerics; // For BigInteger
using System.Text;
using System.Text.RegularExpressions; // For potential string processing
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms; // For SHA256D
using Miningcore.Extensions;
using Miningcore.Native; // For direct RandomX access
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq; // Keep for potential extra data handling
using NLog; // Added for logging

namespace Miningcore.Blockchain.Alpha;

public class AlphaJob : BitcoinJob
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger(); // Added logger instance

    // Alpha/RandomX specific fields
    protected readonly string randomxRealm = "alpha"; // Realm identifier for RandomX VM pool
    protected uint rxEpochDuration; // Epoch duration (in seconds) from the daemon
    protected byte[] seedHash; // 32-byte seed hash for RandomX key
    protected string seedHashHex; // Hex encoded seed hash for RandomX key lookup

    // Static flag to ensure test block is only generated once
    private static bool testBlockGenerated = false;
    
    // Alpha-specific Diff1 value for RandomX hashing
    // Based on Alpha daemon's GetDifficulty function which uses 0x000fffff coefficient
    // This is used for share difficulty calculations and is critical for correctly
    // validating miner submissions
    protected static readonly BigInteger AlphaDiff1 = BigInteger.Parse("0000000fffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

    // Constants for header layout
    protected const int HASHING_BLOB_SIZE = 80;
    protected const int NONCE_OFFSET = 76; // Nonce starts at byte 76 in the 80-byte header
    protected const int HEADER_SIZE = 112; // nVersion (4) + PrevBlock (32) + Merkle (32) + Time (4) + Bits (4) + Nonce (4) + RandomXHash (32)
    protected const int RANDOMX_HASH_OFFSET = 80; // RandomX result hash starts after the standard 80 bytes

    /// <summary>
    /// Initializes the job with Alpha-specific parameters.
    /// Uses the base.Init method and adds Alpha-specific RandomX setup.
    /// </summary>
    public void InitAlpha(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm blockHasher, // Final block hash (SHA256D)
        uint rxEpochDuration) // Pass in epoch duration
    {
        logger.Info(() => $"AlphaJob.InitAlpha called with blockTemplate height={blockTemplate?.Height}, jobId={jobId}, rxEpochDuration={rxEpochDuration}");
        
        try
        {
            Contract.RequiresNonNull(blockTemplate);
            Contract.Requires<ArgumentException>(rxEpochDuration > 0);
            
            // Validate critical fields in the blockTemplate
            if (blockTemplate.Extra == null)
            {
                logger.Error(() => "blockTemplate.Extra is null in InitAlpha - this is critical as rx_epoch_duration must be present");
            }
            else
            {
                logger.Debug(() => $"blockTemplate.Extra keys: {string.Join(", ", blockTemplate.Extra.Keys)}");
                
                if (!blockTemplate.Extra.ContainsKey("rx_epoch_duration"))
                {
                    logger.Error(() => "blockTemplate.Extra does not contain rx_epoch_duration key - this should match the provided parameter");
                }
                else
                {
                    var epochDurationValue = blockTemplate.Extra["rx_epoch_duration"];
                    logger.Debug(() => $"blockTemplate.Extra[\"rx_epoch_duration\"] = {epochDurationValue} (Type: {epochDurationValue?.GetType().Name})");
                    
                    if (epochDurationValue != null)
                    {
                        // Verify that the value matches our parameter
                        if (epochDurationValue is JToken jToken)
                        {
                            var tokenValue = jToken.Value<uint>();
                            if (tokenValue != rxEpochDuration)
                            {
                                logger.Warn(() => $"rx_epoch_duration mismatch! Parameter: {rxEpochDuration}, Template: {tokenValue}");
                            }
                            else
                            {
                                logger.Debug(() => $"rx_epoch_duration values match: {rxEpochDuration}");
                            }
                        }
                    }
                }
            }

            // Call base initialization - handles most standard Bitcoin setup
            base.Init(blockTemplate, jobId, pc, extraPoolConfig, cc, clock, 
                poolAddressDestination, network, isPoS, shareMultiplier, 
                coinbaseHasher, 
                new Crypto.Hashing.Algorithms.Null(), // Header hasher is not used for Alpha - RandomX is used instead
                blockHasher); // For final block hash

            // Store Alpha specifics
            this.rxEpochDuration = rxEpochDuration;
            
            // Calculate network difficulty directly from bits using Alpha daemon's formula
            // This matches the GetDifficulty function in the Alpha daemon exactly
            var bits = Convert.ToUInt32(BlockTemplate.Bits, 16);
            
            var nShift = (bits >> 24) & 0xff;
            var nCoefficient = bits & 0x00ffffff;
            
            // Alpha uses 0x000fffff as the base coefficient (from daemon code)
            var dDiff = (double)0x000fffff / (double)nCoefficient;
            
            // Apply nShift adjustment (same as daemon)
            while (nShift < 29) {
                dDiff *= 256.0;
                nShift++;
            }
            while (nShift > 29) {
                dDiff /= 256.0;
                nShift--;
            }
            
            // Set the network difficulty without any additional scaling
            this.Difficulty = Math.Max(0, dDiff);

            // --- Alpha Specific RandomX Setup ---
            // Calculate RandomX Seed Hash based on epoch
            var epoch = BlockTemplate.CurTime / this.rxEpochDuration;
            var seedString = $"Alpha/RandomX/Epoch/{epoch}";
            var seedBytes = Encoding.UTF8.GetBytes(seedString);

            // Calculate SHA256D (SHA256(SHA256(data)))
            this.seedHash = new byte[32];
            var sha256d = new Sha256D();
            sha256d.Digest(seedBytes, this.seedHash);
            
            this.seedHashHex = this.seedHash.ToHexString();

            // Initialize RandomX environment for this seed hash if needed
            // Use -1 to utilize all available CPU cores for maximum performance
            Native.RandomX.CreateSeed(randomxRealm, seedHashHex, vmCount: -1);
            logger.Info(() => $"Used RXEpoch {epoch} to calculate RandomX seed {seedHashHex} for realm {randomxRealm}");
            
            // Store the pool address script bytes for coinbase transaction creation
            poolAddressScript = poolAddressDestination.ScriptPubKey.ToBytes(true);
            
            // Generate a test transaction to verify format, but only once
            if (!testBlockGenerated)
            {
                try
                {
                    // Create a dummy header with all fields zeroed
                    byte[] dummyHeader = new byte[HASHING_BLOB_SIZE]; // 80-byte standard header
                    byte[] dummyRandomX = new byte[32]; // 32-byte randomX hash

                    // Create full 112-byte header
                    byte[] fullHeader = new byte[HEADER_SIZE];
                    Buffer.BlockCopy(dummyHeader, 0, fullHeader, 0, dummyHeader.Length);
                    Buffer.BlockCopy(dummyRandomX, 0, fullHeader, dummyHeader.Length, dummyRandomX.Length);

                    // Create a coinbase transaction with dummy values
                    string dummyExtraNonce1 = "11111111";
                    string dummyExtraNonce2 = "22222222";
                    byte[] coinbaseTx = SerializeCoinbase(dummyExtraNonce1, dummyExtraNonce2);

                    // Create and log a test block
                    string testBlock = SerializeBlockForSubmission(fullHeader, coinbaseTx);
                    logger.Info(() => $"=== ALPHA_TEST_BLOCK_AT_STARTUP ===");
                    logger.Info(() => $"Test block hex (truncated): {testBlock.Substring(0, Math.Min(500, testBlock.Length))}...");

                    // Save test block to file
                    try
                    {
                        var blocksDir = Path.Combine(
                            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                            "alpha-blocks");

                        if (!Directory.Exists(blocksDir))
                            Directory.CreateDirectory(blocksDir);

                        var testBlockPath = Path.Combine(
                            blocksDir,
                            $"alpha-test-block-{DateTime.UtcNow:yyyyMMdd-HHmmss}.hex");

                        File.WriteAllText(testBlockPath, testBlock);
                        logger.Info(() => $"Saved test block to: {testBlockPath}");

                        // Set flag to prevent future test block generation
                        testBlockGenerated = true;
                        logger.Info(() => $"Test block generation complete - will not generate additional test blocks");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(() => $"Failed to save test block: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(() => $"Failed to generate test block: {ex.Message}");
                }
            }
            else
            {
                logger.Debug(() => $"Skipping test block generation - already generated previously");
            }
            
            logger.Info(() => $"Alpha job initialized with accurate coinbase transaction format");
        }
        catch (Exception ex)
        {
            logger.Error(() => $"Exception in AlphaJob.InitAlpha: {ex.Message}");
            logger.Error(() => $"Exception type: {ex.GetType().Name}");
            logger.Error(() => $"Stack trace: {ex.StackTrace}");
            throw; // Re-throw to ensure proper error handling
        }
    }




    /// <summary>
    /// Returns job parameters using the standard Bitcoin format.
    /// Uses 'new' instead of 'override' because the base method is not virtual.
    /// </summary>
    public new object GetJobParams(bool isNew)
    {
        logger.Debug(() => $"AlphaJob.GetJobParams(isNew={isNew}) called");
        
        // Check job initialization status
        if (jobParams == null)
        {
            logger.Error(() => $"AlphaJob.GetJobParams called but jobParams is null. This job may not be fully initialized.");
            logger.Error(() => $"BlockTemplate is {(BlockTemplate == null ? "NULL" : "set")}, JobId={JobId}");
            
            // Check for rx_epoch_duration which is a critical parameter for Alpha
            if (BlockTemplate != null)
            {
                logger.Debug(() => $"BlockTemplate details: Height={BlockTemplate.Height}, PrevHash={BlockTemplate.PreviousBlockhash?.Substring(0, 8)}");
                
                if (BlockTemplate.Extra == null)
                {
                    logger.Error(() => "BlockTemplate.Extra is NULL - this is critical since rx_epoch_duration should be there");
                }
                else if (!BlockTemplate.Extra.TryGetValue("rx_epoch_duration", out var epochDurationObj))
                {
                    logger.Error(() => "BlockTemplate.Extra does not contain rx_epoch_duration - this is a critical issue");
                    logger.Debug(() => $"BlockTemplate.Extra keys: {string.Join(", ", BlockTemplate.Extra.Keys)}");
                }
                else
                {
                    logger.Debug(() => $"rx_epoch_duration is present: {epochDurationObj}");
                }
            }
            
            return null;
        }
        
        var result = base.GetJobParams(isNew);
        logger.Debug(() => $"AlphaJob.GetJobParams returning {(result == null ? "NULL" : "valid")} result");
        
        if (result != null && result is object[] resultArray && resultArray.Length > 0)
        {
            logger.Debug(() => $"Result jobId={resultArray[0]}, array length={resultArray.Length}");
        }
        
        return result;
    }

    /// <summary>
    /// Overridden to process shares using RandomX validation.
    /// </summary>
    public override (Share Share, string BlockHex) ProcessShare(StratumConnection worker,
        string extraNonce2, string nTime, string nonce, string versionBits = null)
    {
        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce2));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nTime));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // Validate nTime (reusable from base)
        if(nTime.Length != 8) throw new StratumException(StratumError.Other, "incorrect size of ntime");
        var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);
        if(nTimeInt < BlockTemplate.CurTime || nTimeInt > ((DateTimeOffset) clock.Now).ToUnixTimeSeconds() + 7200)
            throw new StratumException(StratumError.Other, "ntime out of range");

        // Validate nonce (reusable from base)
        if(nonce.Length != 8) throw new StratumException(StratumError.Other, "incorrect size of nonce");
        var nonceInt = uint.Parse(nonce, NumberStyles.HexNumber);

        // Validate version-bits (reusable from base, if version rolling is enabled - keep for safety check)
        if(context.VersionRollingMask.HasValue && versionBits != null)
        {
            var parsedVersionBits = uint.Parse(versionBits, NumberStyles.HexNumber);
            if((parsedVersionBits & ~context.VersionRollingMask.Value) != 0)
                throw new StratumException(StratumError.Other, "rolling-version mask violation");
            // We don't store versionBitsInt as it's not used for Alpha's header construction
        }

        // Dupe check (reusable from base)
        if(!RegisterSubmit(context.ExtraNonce1, extraNonce2, nTime, nonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        // --- Alpha / RandomX Validation ---
        // Pass null for versionBits as it's not used in the final header construction for Alpha
        return ProcessShareInternal(worker, extraNonce2, nTimeInt, nonceInt, null);
    }



        
    // Storage for pool's address script - initialized during Init
    private byte[] poolAddressScript;
    
 
    
    protected override (Share Share, string BlockHex) ProcessShareInternal(
        StratumConnection worker, string extraNonce2, uint nTime, uint nonce, uint? versionBits)
    {
        var context = worker.ContextAs<BitcoinWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        var coinbase = SerializeCoinbase(extraNonce1, extraNonce2);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // 1. Let Bitcoin code handle most of the work of creating the header
        // SerializeHeader builds a standard 80-byte Bitcoin header with coinbase and merkle root
        var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce, context.VersionRollingMask, versionBits);
        
        // 2. First calculate the RandomX hash with a full 112-byte header that has the hash field nulled
        // This matches exactly what the daemon does when calculating the RandomX hash
        
        // Create a full 112-byte header with RandomX hash field set to zeros
        byte[] fullHeaderWithNullHash = new byte[HEADER_SIZE]; // 112 bytes
        
        // Copy the standard 80-byte header
        Array.Copy(headerBytes, 0, fullHeaderWithNullHash, 0, headerBytes.Length);
        
        // The remaining 32 bytes (hash field) are already zeros (default value for new byte array)
        
        Span<byte> headerHashRandomX = stackalloc byte[32];
        Native.RandomX.CalculateHash(randomxRealm, seedHashHex, fullHeaderWithNullHash, headerHashRandomX);


        // Record the RandomX hash for debugging and block building
        var randomXValue = new uint256(headerHashRandomX);
        logger.Debug(() => $"RandomX hash: {randomXValue}");
        
        // 3. Then calculate the commitment hash using the same full 112-byte header with RandomX hash set to null
        Span<byte> commitmentHash = stackalloc byte[32];
        
        // We can reuse the same fullHeaderWithNullHash since it already has:
        // - The 80-byte header in the first 80 bytes
        // - Zeros in the last 32 bytes (hash field)
        
        // Now calculate the commitment using the full header (with nulled hash field) and the RandomX hash
        Native.RandomX.CalculateCommitment(fullHeaderWithNullHash, headerHashRandomX, commitmentHash);
        
        logger.Debug(() => $"Calculated commitment using modified header and RandomX hash, following daemon behavior");
  
        
        // The commitment hash is used for difficulty validation
        var commitmentValue = new uint256(commitmentHash);
        logger.Debug(() => $"RandomX commitment hash: {commitmentValue}");
        
        // To explain what this calculation does:
        // 1. AlphaDiff1 is the maximum difficulty target (0x000fffff...0)
        // 2. commitmentHash.ToBigInteger() converts the commitment hash to a numeric value for calculation
        // 3. The division AlphaDiff1/hash gives us difficulty - lower hash = higher difficulty
        // 4. shareMultiplier is applied to normalize difficulty (typically 1.0 for most chains)
        
        var shareDifficulty = (double) new BigRational(AlphaDiff1, commitmentHash.ToBigInteger()) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDifficulty / stratumDifficulty;
        
        logger.Debug(() => $"Share calculation: commitment={commitmentValue}, diff1={AlphaDiff1}, " + 
                          $"shareDiff={shareDifficulty}, stratumDiff={stratumDifficulty}, ratio={ratio}, target={blockTargetValue}");

        // 4. Check if the share meets the block target using the commitment hash
        var isBlockCandidate = commitmentValue <= blockTargetValue;

        // 5. Check if the share meets the worker's difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            // Check against previous difficulty if vardiff is enabled (copied from base)
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDifficulty / context.PreviousDifficulty.Value;
                if(ratio < 0.99) throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDifficulty})");
                stratumDifficulty = context.PreviousDifficulty.Value; // Use previous difficulty
            }
            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDifficulty})");
        }

        // 6. Create the Share object
        var result = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty, // Network difficulty for the block template
            Difficulty = stratumDifficulty / shareMultiplier, // Difficulty for this specific share
        };       
        
        // 7. If it's a block candidate, build the full block
        if(isBlockCandidate)
        {
            result.IsBlockCandidate = true;
            result.BlockReward = rewardToPool.ToDecimal(MoneyUnit.BTC);

            var fullHeaderBytes = BuildBlockHeaderBytes(worker, extraNonce2, nTime, nonce, versionBits, headerHashRandomX.ToArray());
            var coinbaseTxBytes = SerializeCoinbase(extraNonce1, extraNonce2);

            // Calculate the final block hash (SHA256D of the 112-byte header) for the share result
            Span<byte> finalBlockHash = stackalloc byte[32];

            // Pass nTime parameter to match standard BitcoinJob implementation
            this.blockHasher.Digest(fullHeaderBytes, finalBlockHash, nTime);

            // Explicitly reverse the bytes to match Alpha daemon's little-endian format
            // This ensures the hash matches what the daemon returns for verification
            byte[] originalHash = finalBlockHash.ToArray(); // Make a copy for logging
            byte[] reversedHash = originalHash.ToArray();   // Another copy for reversing
            Array.Reverse(reversedHash);
            result.BlockHash = reversedHash.ToHexString();

            logger.Info(() => $"Generated block hash: {result.BlockHash} (original byte order: {originalHash.ToHexString()})");

            // Calculate the actual version with version bits if applicable
            uint actualVersion = BlockTemplate.Version;
            if (versionBits.HasValue && context.VersionRollingMask.HasValue)
            {
                actualVersion = (actualVersion & ~context.VersionRollingMask.Value) | (versionBits.Value & context.VersionRollingMask.Value);
            }

     
            // Create Alpha-specific formatted block for submission
            string alphaFormattedBlockHex = null;
            alphaFormattedBlockHex = SerializeBlockForSubmission(fullHeaderBytes, coinbaseTxBytes);

            return (result, alphaFormattedBlockHex);
        }

        return (result, null);
    }
    

   

    protected byte[] BuildBlockHeaderBytes(StratumConnection worker, string extraNonce2, 
        uint nTime, uint nonce, uint? versionBits, byte[] randomXHash)
    {

        var context = worker.ContextAs<BitcoinWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        // build coinbase
        var coinbase = SerializeCoinbase(extraNonce1, extraNonce2);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);


        // First generate the standard 80-byte Bitcoin header
        var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce, context.VersionRollingMask, versionBits);
        
        // Create a 112-byte header that also contains the randomx hash
        using var stream = new MemoryStream(HEADER_SIZE);
        
        // Copy the standard 80-byte header
        stream.Write(headerBytes, 0, headerBytes.Length);
        
        // Append the 32-byte RandomX hash
        stream.Write(randomXHash, 0, randomXHash.Length);
        
        if(stream.Position != HEADER_SIZE)
            throw new InvalidOperationException($"Full block header size mismatch: expected {HEADER_SIZE}, got {stream.Position}");
            
        return stream.ToArray();
    }
    

    
    /// <summary>
    /// Implementation of SerializeBlockForSubmission that uses the base BitcoinJob.SerializeBlock method.
    /// </summary>
    public string SerializeBlockForSubmission(byte[] fullHeader, byte[] coinbaseTx)
    {
        if (fullHeader == null || fullHeader.Length != HEADER_SIZE)
            throw new ArgumentException($"fullHeader must be exactly {HEADER_SIZE} bytes", nameof(fullHeader));
        if (coinbaseTx == null)
            throw new ArgumentException("coinbaseTx cannot be null", nameof(coinbaseTx));

        logger.Info(() => $" Preparing Alpha block using base class serialization");

        // Debug logging for coinbase transaction format
        if (coinbaseTx.Length > 8)
        {
            logger.Info(() => $"Coinbase TX debug - First 16 bytes: {BitConverter.ToString(coinbaseTx, 0, Math.Min(16, coinbaseTx.Length))}");
            logger.Info(() => $"Coinbase TX debug - Total length: {coinbaseTx.Length} bytes");

            // Check for sequence value at expected position (will depend on the variable script length)
            int scriptLenPos = 41;
            if (scriptLenPos < coinbaseTx.Length)
            {
                byte scriptLen = coinbaseTx[scriptLenPos];
                int sequencePos = scriptLenPos + 1 + scriptLen;
                if (sequencePos + 4 <= coinbaseTx.Length)
                {
                    uint sequence = BitConverter.ToUInt32(coinbaseTx, sequencePos);
                    logger.Info(() => $"Coinbase TX debug - Sequence value: 0x{sequence:X8}");
                }
            }

            // Check if locktime is present at the end
            if (coinbaseTx.Length >= 4)
            {
                uint locktime = BitConverter.ToUInt32(coinbaseTx, coinbaseTx.Length - 4);
                logger.Info(() => $"Coinbase TX debug - Last 4 bytes (potential locktime): 0x{locktime:X8}");
            }
        }
        
        // Extract header components
        var standardHeader = new byte[HASHING_BLOB_SIZE]; // 80-byte standard header
        Buffer.BlockCopy(fullHeader, 0, standardHeader, 0, HASHING_BLOB_SIZE);
        
        var randomXHash = new byte[32]; // 32-byte RandomX hash
        Buffer.BlockCopy(fullHeader, HASHING_BLOB_SIZE, randomXHash, 0, 32);
                
        // Create a custom AlphaBlock format that includes the RandomX hash
        using var blockStream = new MemoryStream();

        // 1. Write the standard 80-byte header
        blockStream.Write(standardHeader, 0, standardHeader.Length);
        logger.Debug(() => $"SerializeBlockForSubmission: Added standard header ({standardHeader.Length} bytes)");

        // 2. Add the 32-byte RandomX hash
        blockStream.Write(randomXHash, 0, randomXHash.Length);
        logger.Debug(() => $"SerializeBlockForSubmission: Added RandomX hash ({randomXHash.Length} bytes)");

        // Skip the first 80 bytes (standard header) to get just the transaction data
        // This includes: tx count VarInt + coinbase tx + other txs
        int txDataOffset = HASHING_BLOB_SIZE; // 80 bytes

        // 3. Get the transaction portion of the block using base class method
        // We create a "virtual" standard block with just the standard header
        byte[] standardBlock = base.SerializeBlock(standardHeader, coinbaseTx);
        logger.Info(() => $"SerializeBlockForSubmission: Base SerializeBlock returned {standardBlock.Length} bytes");

        // Verify coinbase transaction in standardBlock
        if (standardBlock.Length >= txDataOffset + 10)
        {
            // Check tx count (should be at least 1)
            byte txCount = standardBlock[txDataOffset];
            logger.Debug(() => $"SerializeBlockForSubmission: Transaction count: {txCount}");

            // Check if we have sufficient data for locktime at the end of the block
            if (standardBlock.Length >= txDataOffset + 100)
            {
                // Find the position where the coinbase TX ends by looking at the structure
                // This will help us verify if locktime is present
                logger.Debug(() => $"SerializeBlockForSubmission: Transaction data starts at offset {txDataOffset}, length={standardBlock.Length - txDataOffset}");

                // Analyze the last few bytes to see if locktime is missing
                var lastBytes = new byte[4];
                Array.Copy(standardBlock, standardBlock.Length - 4, lastBytes, 0, 4);
                logger.Info(() => $"Last 4 bytes of standardBlock: 0x{BitConverter.ToUInt32(lastBytes, 0):X8}");

                // Check if the second-to-last transaction output ends properly
                if (standardBlock.Length >= txDataOffset + 150)
                {
                    // Attempt to find the end of the outputs section by checking patterns
                    for (int i = standardBlock.Length - 20; i >= txDataOffset + 100; i--)
                    {
                        if (i + 4 <= standardBlock.Length)
                        {
                            var potential = BitConverter.ToUInt32(standardBlock, i);
                            if (potential == 0)
                            {
                                logger.Info(() => $"Potential locktime at offset {i}: 0x{potential:X8}");
                                break;
                            }
                        }
                    }
                }
            }
        }
        int txDataLength = standardBlock.Length - txDataOffset;

        if (txDataLength <= 0)
        {
            logger.Error(() => $"SerializeBlockForSubmission: Invalid transaction data length: {txDataLength}");
            throw new InvalidOperationException("Generated block has no transaction data");
        }

        // Extract transaction data including the locktime field
        var txData = new byte[txDataLength];
        Buffer.BlockCopy(standardBlock, txDataOffset, txData, 0, txDataLength);

        // Ensure the locktime field is present at the end
        logger.Info(() => $"Checking transaction data for locktime field");
        var containsLocktime = false;
        if (txData.Length >= 4)
        {
            var potentialLocktime = BitConverter.ToUInt32(txData, txData.Length - 4);
            logger.Info(() => $"Last 4 bytes of transaction data: 0x{potentialLocktime:X8}");

            // Locktime is typically 0 for coinbase transactions
            if (potentialLocktime == 0)
            {
                containsLocktime = true;
                logger.Info(() => $"Transaction data appears to include locktime field (0x00000000)");
            }
        }

        // If locktime is missing, add it
        if (!containsLocktime)
        {
            logger.Warn(() => $"Locktime field may be missing, manually adding it");

            // Create a new transaction data buffer with locktime
            var txDataWithLocktime = new byte[txData.Length + 4];
            Buffer.BlockCopy(txData, 0, txDataWithLocktime, 0, txData.Length);
            // Append locktime (4 bytes of zeros)
            BitConverter.GetBytes(0u).CopyTo(txDataWithLocktime, txData.Length);

            // Use the new buffer
            txData = txDataWithLocktime;
            logger.Info(() => $"Modified transaction data length: {txData.Length} bytes (added locktime)");
        }

        blockStream.Write(txData, 0, txData.Length);
        logger.Debug(() => $"SerializeBlockForSubmission: Added transaction data ({txData.Length} bytes)");

        // Generate the full block hex
        var result = blockStream.ToArray().ToHexString();
        logger.Info(() => $"SerializeBlockForSubmission: Generated block of {result.Length/2} bytes");
        
            
        return result;
    }
}

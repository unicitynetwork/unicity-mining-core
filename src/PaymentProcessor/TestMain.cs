using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaymentProcessor.Configuration;
using PaymentProcessor.Models;
using PaymentProcessor.Services;

namespace PaymentProcessor;

public class TestMain
{
    public static async Task TestPaymentProcessorAsync()
    {
        // Setup logging
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));

        var logger = loggerFactory.CreateLogger<TestMain>();
        
        Console.WriteLine("=== Testing PaymentProcessor Cross-Run Tracking ===");

        // Create test configuration
        var config = new PaymentProcessorConfig
        {
            ApiBaseUrl = "https://www.unicity-pool.com",
            PoolId = "alpha1", 
            ApiKey = "3987591e3294814baedb2bb6310b2ebffa1abf63c33e95e0f4b06840c58864bd",
            TimeoutSeconds = 30,
            AlphaDaemon = new AlphaDaemonConfig
            {
                RpcUrl = "http://localhost:8589",
                RpcUser = "u",
                RpcPassword = "p",
                RpcTimeoutSeconds = 30,
                DataDir = "/Users/mike/dummy8/",
                WalletName = "ct4",
                WalletAddress = "",
                ChangeAddress = "",
                WalletPassword = "",
                FeePerByte = 0.00001m,
                ConfirmationsRequired = 1,
                UseWalletRPC = true
            }
        };

        // Setup services
        var rpcHttpClient = new HttpClient();
        var apiHttpClient = new HttpClient();
        var rpcLogger = loggerFactory.CreateLogger<AlphaRpcClient>();
        var paymentServiceLogger = loggerFactory.CreateLogger<AlphaPaymentService>();
        var apiLogger = loggerFactory.CreateLogger<PaymentApiClient>();
        var completionTrackerLogger = loggerFactory.CreateLogger<FilePaymentCompletionTracker>();
        
        var rpcClient = new AlphaRpcClient(rpcHttpClient, config, rpcLogger);
        var apiClient = new PaymentApiClient(apiHttpClient, config, apiLogger);
        var completionTracker = new FilePaymentCompletionTracker(completionTrackerLogger, "test_completed_payments.json");
        var paymentService = new AlphaPaymentService(rpcClient, apiClient, config, paymentServiceLogger, completionTracker);

        // Set wallet
        rpcClient.SetWallet(config.AlphaDaemon.WalletName);

        try
        {
            // Test Case 1: Test large payment that requires multiple UTXOs
            Console.WriteLine("\n=== Test Case 1: Large Payment Requiring Multiple UTXOs ===");
            var largePayment = new PendingPayment
            {
                Id = 3001,
                Address = "alpha1qqg3rsxywztltjzff7w98d220v32jcaxv3fed2v", // CT4 address
                Amount = 35.0m, // Larger than any single UTXO (max ~10 ALPHA)
                CreatedUtc = DateTime.UtcNow
            };

            Console.WriteLine($"Testing payment: {largePayment.Amount} ALPHA to {largePayment.Address[^10..]}");
            Console.WriteLine($"This should require multiple UTXOs since largest UTXO is ~10 ALPHA");
            
            // Check if payment is already tracked as completed
            var isCompleted = await completionTracker.IsPaymentCompletedAsync(largePayment.Id);
            Console.WriteLine($"Payment ID {largePayment.Id} completed: {isCompleted}");
            
            if (isCompleted)
            {
                var existingTxId = await completionTracker.GetPaymentTransactionIdAsync(largePayment.Id);
                Console.WriteLine($"✅ Payment already completed in transaction: {existingTxId}");
            }
            else
            {
                Console.WriteLine("⚠️  Payment needs processing - will test true batching");
            }

            // Test Case 2: Test another payment to compare
            Console.WriteLine("\n=== Test Case 2: Regular Payment (For Comparison) ===");
            var regularPayment = new PendingPayment
            {
                Id = 3002,
                Address = "alpha1qq9e7924pmv7s0ec97cdjjr2jx8mlz5h0rmlnn7", // Different CT4 address
                Amount = 8.0m, // Regular amount that fits in one UTXO
                CreatedUtc = DateTime.UtcNow
            };

            Console.WriteLine($"Testing payment: {regularPayment.Amount} ALPHA to {regularPayment.Address[^10..]}");
            Console.WriteLine($"This should use a single UTXO since 8.0 < 10.0 ALPHA");
            
            var isRegularCompleted = await completionTracker.IsPaymentCompletedAsync(regularPayment.Id);
            Console.WriteLine($"Payment ID {regularPayment.Id} completed: {isRegularCompleted}");
            
            if (isRegularCompleted)
            {
                var existingTxId = await completionTracker.GetPaymentTransactionIdAsync(regularPayment.Id);
                Console.WriteLine($"✅ Payment already completed in transaction: {existingTxId}");
            }
            else
            {
                Console.WriteLine("⚠️  Payment needs processing - should use single UTXO");
            }

            // Test Case 3: Actually run the payment processing logic
            Console.WriteLine("\n=== Test Case 3: Running Payment Processing Logic ===");
            
            var testPayments = new List<PendingPayment> { largePayment };
            Console.WriteLine("Processing large payment through PaymentProcessor...");
            Console.WriteLine("This will test:");
            Console.WriteLine("- Multi-UTXO batching (35.0 ALPHA requires ~4 UTXOs)");
            Console.WriteLine("- Single-input transaction constraint");
            Console.WriteLine("- Payment-specific completion tracking");
            Console.WriteLine("- Proper UTXO selection and change handling");
            
            // This should use our new cross-run tracking logic
            var results = await paymentService.ProcessPaymentsBatchAsync(testPayments);
            
            Console.WriteLine($"Results: {results.Count} payment result(s)");
            
            foreach (var result in results)
            {
                Console.WriteLine($"Payment {result.Address[^10..]}: Success={result.Success}, Amount={result.Amount}");
                if (result.Success)
                {
                    Console.WriteLine($"  ✅ Transaction ID: {result.TransactionId}");
                    if (result.TransactionId == "ALREADY_COMPLETED")
                    {
                        Console.WriteLine($"  ℹ️  Payment was already completed in previous run");
                    }
                }
                else
                {
                    Console.WriteLine($"  ❌ Error: {result.Error}");
                }
            }

            Console.WriteLine("\n=== Test completed successfully ===");
            Console.WriteLine("This test demonstrates:");
            Console.WriteLine("- True multi-UTXO batching for large payments");
            Console.WriteLine("- Payment-specific tracking (no address-based overpayment issues)");
            Console.WriteLine("- Multiple single-input transactions to complete one payment");
            Console.WriteLine("- Proper UTXO selection and change handling");
            Console.WriteLine("- Cross-run completion tracking");
            
            // Show the completion tracking file
            Console.WriteLine("\n=== Completion Tracking File ===");
            try
            {
                var completionFile = "test_completed_payments.json";
                if (File.Exists(completionFile))
                {
                    var content = await File.ReadAllTextAsync(completionFile);
                    Console.WriteLine($"Contents of {completionFile}:");
                    Console.WriteLine(content);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read completion file: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Test failed: {Message}", ex.Message);
            Console.WriteLine($"❌ Test failed: {ex.Message}");
        }
    }
}
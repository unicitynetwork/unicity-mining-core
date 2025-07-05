using Microsoft.Extensions.Logging;
using PaymentProcessor.Configuration;
using PaymentProcessor.Services;

namespace PaymentProcessor;

public class PaymentProcessorApp
{
    private readonly PaymentProcessorConfig _config;
    private readonly IPaymentApiClient _apiClient;
    private readonly IConsoleService _console;
    private readonly IPaymentProcessor _paymentProcessor;
    private readonly IAlphaRpcClient _alphaRpcClient;
    private readonly ILogger<PaymentProcessorApp> _logger;

    public PaymentProcessorApp(
        PaymentProcessorConfig config,
        IPaymentApiClient apiClient,
        IConsoleService console,
        IPaymentProcessor paymentProcessor,
        IAlphaRpcClient alphaRpcClient,
        ILogger<PaymentProcessorApp> logger)
    {
        _config = config;
        _apiClient = apiClient;
        _console = console;
        _paymentProcessor = paymentProcessor;
        _alphaRpcClient = alphaRpcClient;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            _console.DisplayWelcome();
            
            _logger.LogInformation("Starting Payment Processor");
            _logger.LogInformation("Configuration - API: {ApiUrl}, Pool: {PoolId}", 
                _config.ApiBaseUrl, _config.PoolId);

            // Test API connection
            var isConnected = await _apiClient.TestConnectionAsync();
            _console.DisplayConnectionStatus(isConnected, _config.ApiBaseUrl);

            if (!isConnected)
            {
                _console.DisplayError("Cannot connect to API. Please check your configuration and ensure the Miningcore server is running.");
                _console.DisplayInfo("Note: Admin API endpoints may be IP-restricted. See SETUP.md for configuration guidance.");
                return;
            }

            // Alpha daemon connection and wallet configuration
            await ConfigureWalletAsync();

            // Main processing loop
            while (true)
            {
                try
                {
                    // Fetch pending payments
                    _console.DisplayInfo("Fetching pending payments...");
                    var pendingPayments = await _apiClient.GetPendingPaymentsAsync(_config.PoolId);
                    
                    // Display payments
                    _console.DisplayPendingPayments(pendingPayments);
                    
                    if (pendingPayments.Count == 0)
                    {
                        _console.DisplayInfo("No pending payments found. Exiting...");
                        break;
                    }

                    // Select payments to process
                    var selectedPayments = _console.SelectPayments(pendingPayments);
                    
                    if (selectedPayments.Count == 0)
                    {
                        _console.DisplayInfo("No payments selected. Exiting...");
                        break;
                    }

                    // Confirm processing
                    var confirmed = _console.ConfirmProcessing(selectedPayments);
                    
                    if (!confirmed)
                    {
                        _console.DisplayInfo("Processing cancelled. Exiting...");
                        break;
                    }

                    // Process payments
                    var results = await _paymentProcessor.ProcessPaymentsAsync(selectedPayments);
                    
                    // Display results
                    _console.DisplayProcessingResults(results);
                    
                    // Ask if user wants to continue
                    var continueProcessing = selectedPayments.Count < pendingPayments.Count;
                    if (continueProcessing)
                    {
                        _console.DisplayInfo("Processing completed. Check for more pending payments...");
                        await Task.Delay(2000); // Brief pause before next iteration
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in processing loop");
                    _console.DisplayError($"Processing error: {ex.Message}");
                    
                    // Ask if user wants to retry
                    break;
                }
            }

            _console.DisplayInfo("Payment processing completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Payment Processor");
            _console.DisplayError($"Fatal error: {ex.Message}");
            throw;
        }
    }

    private async Task ConfigureWalletAsync()
    {
        try
        {
            _console.DisplayInfo("Testing Alpha daemon connection...");
            
            var daemonConnected = await _alphaRpcClient.TestConnectionAsync();
            if (!daemonConnected)
            {
                _console.DisplayError("Cannot connect to Alpha daemon. Check configuration and ensure daemon is running.");
                throw new InvalidOperationException("Alpha daemon connection failed");
            }

            var configuredWalletName = _config.AlphaDaemon.WalletName;
            if (string.IsNullOrEmpty(configuredWalletName))
            {
                _console.DisplayError("No wallet configured. Please set 'AlphaDaemon.WalletName' in appsettings.json");
                throw new InvalidOperationException("No wallet configured");
            }

            _console.DisplayInfo($"Configuring wallet: {configuredWalletName}");
            
            // Set the wallet in RPC client first
            _alphaRpcClient.SetWallet(configuredWalletName);
            
            // Verify wallet exists by trying to get wallet info
            // This will fail if the wallet doesn't exist
            try
            {
                var walletNames = await _alphaRpcClient.ListWalletsAsync();
                if (!walletNames.Contains(configuredWalletName))
                {
                    _console.DisplayError($"Configured wallet '{configuredWalletName}' not found in available wallets: {string.Join(", ", walletNames)}");
                    throw new InvalidOperationException($"Wallet '{configuredWalletName}' not found");
                }
            }
            catch (Exception ex)
            {
                _console.DisplayError($"Could not verify wallet '{configuredWalletName}': {ex.Message}");
                throw new InvalidOperationException($"Wallet '{configuredWalletName}' verification failed");
            }
            _logger.LogInformation("Set RPC client to use wallet: {WalletName}", configuredWalletName);
            
            // Get wallet info and display
            var walletInfo = await _alphaRpcClient.GetWalletInfoAsync(configuredWalletName);
            _console.DisplayWalletInfo(walletInfo);
            
            // Validate wallet has sufficient balance
            var totalPending = await GetTotalPendingAmount();
            if (walletInfo.Balance < totalPending)
            {
                _console.DisplayError($"Configured wallet has insufficient balance. Required: {totalPending:F8}, Available: {walletInfo.Balance:F8}");
                throw new InvalidOperationException("Insufficient wallet balance");
            }

            _logger.LogInformation("Configured wallet {WalletName} with balance {Balance} ALPHA", 
                configuredWalletName, walletInfo.Balance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure wallet");
            throw;
        }
    }

    private async Task<decimal> GetTotalPendingAmount()
    {
        try
        {
            var pendingPayments = await _apiClient.GetPendingPaymentsAsync(_config.PoolId);
            return pendingPayments.Sum(p => p.Amount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not calculate total pending amount: {Error}", ex.Message);
            return 0;
        }
    }
}
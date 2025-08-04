using Microsoft.Extensions.Logging;
using PaymentProcessor.Configuration;
using PaymentProcessor.Models;
using Spectre.Console;

namespace PaymentProcessor.Services;

public class AutomatedPaymentService : IAutomatedPaymentService
{
    private readonly PaymentProcessorConfig _config;
    private readonly IPaymentApiClient _apiClient;
    private readonly IPaymentProcessor _paymentProcessor;
    private readonly IAlphaRpcClient _alphaRpcClient;
    private readonly ILogger<AutomatedPaymentService> _logger;
    
    private int _lastProcessedBlock = 0;
    private DateTime _lastProcessingTime = DateTime.MinValue;
    private bool _isRunning = false;
    private int _totalPaymentsProcessed = 0;
    private decimal _totalAmountProcessed = 0m;
    private DateTime _startTime = DateTime.MinValue;

    public AutomatedPaymentService(
        PaymentProcessorConfig config,
        IPaymentApiClient apiClient,
        IPaymentProcessor paymentProcessor,
        IAlphaRpcClient alphaRpcClient,
        ILogger<AutomatedPaymentService> logger)
    {
        _config = config;
        _apiClient = apiClient;
        _paymentProcessor = paymentProcessor;
        _alphaRpcClient = alphaRpcClient;
        _logger = logger;
    }

    public async Task RunAutomatedPaymentsAsync(CancellationToken cancellationToken = default)
    {
        // Always run when called - the caller determines if automation should run

        _isRunning = true;
        _startTime = DateTime.UtcNow;
        _logger.LogInformation("Starting automated payment processing");
        _logger.LogInformation("Configuration: BatchSize={BatchSize}, BlockPeriod={BlockPeriod}, PollingInterval={PollingInterval}s", 
            _config.Automation.BatchSize, _config.Automation.BlockPeriod, _config.Automation.PollingIntervalSeconds);

        try
        {
            // Initialize last processed block if not set
            if (_lastProcessedBlock == 0)
            {
                var currentBlock = await _alphaRpcClient.GetBlockCountAsync(cancellationToken);
                // Set to current block minus block period so first batch processes immediately
                _lastProcessedBlock = currentBlock - _config.Automation.BlockPeriod;
                _logger.LogInformation("Initialized starting block height: {BlockHeight} (will process first batch immediately)", _lastProcessedBlock);
            }

            await AnsiConsole.Live(CreateStatusTable())
                .StartAsync(async ctx =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            // Update status display
                            var status = await GetStatusAsync(cancellationToken);
                            ctx.UpdateTarget(CreateStatusTable(status));

                            // Check if it's time to process payments
                            if (ShouldProcessPayments(status))
                            {
                                await ProcessPaymentBatchAsync(status, cancellationToken);
                                _lastProcessedBlock = status.CurrentBlockHeight;
                                _lastProcessingTime = DateTime.UtcNow;
                            }

                            // Wait before next check
                            await Task.Delay(TimeSpan.FromSeconds(_config.Automation.PollingIntervalSeconds), cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error in automation loop");
                            
                            // Show error in status table
                            var errorStatus = await GetStatusAsync(cancellationToken);
                            errorStatus = errorStatus with { Status = $"Error: {ex.Message}" };
                            ctx.UpdateTarget(CreateStatusTable(errorStatus));
                            
                            // Wait before retrying
                            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                        }
                    }
                });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Automated payment processing cancelled");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private bool ShouldProcessPayments(AutomationStatus status)
    {
        // Check if we have enough new blocks
        var blocksSinceLastProcessing = status.CurrentBlockHeight - status.LastProcessedBlock;
        if (blocksSinceLastProcessing < _config.Automation.BlockPeriod)
        {
            return false;
        }

        // Check if we have pending payments
        if (status.PendingPaymentsCount == 0)
        {
            return false;
        }

        // Check minimum balance requirement
        if (status.WalletBalance < _config.Automation.MinimumBalance)
        {
            _logger.LogWarning("Insufficient wallet balance: {Balance} < {MinBalance}", 
                status.WalletBalance, _config.Automation.MinimumBalance);
            return false;
        }

        return true;
    }

    private async Task ProcessPaymentBatchAsync(AutomationStatus status, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Processing payment batch at block {BlockHeight}", status.CurrentBlockHeight);
            
            // Get pending payments
            var pendingPayments = await _apiClient.GetPendingPaymentsAsync(_config.PoolId, cancellationToken);
            
            // Take batch size
            var paymentsToProcess = pendingPayments.Take(_config.Automation.BatchSize).ToList();
            
            if (paymentsToProcess.Count == 0)
            {
                _logger.LogInformation("No payments to process");
                return;
            }

            _logger.LogInformation("Processing {Count} payments in batch", paymentsToProcess.Count);
            
            // Process the payments without progress bar to avoid UI conflicts
            var results = await _paymentProcessor.ProcessPaymentsAsync(paymentsToProcess, showProgress: false, cancellationToken);
            
            // Log results and update statistics
            var successful = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);
            var partial = results.Count(r => r.IsPartialPayment);
            
            // Update totals (count successful and partial payments)
            var processedCount = successful + partial;
            var processedAmount = results.Where(r => r.Success || r.IsPartialPayment)
                                        .Sum(r => r.IsPartialPayment ? r.CompletedAmount : r.Amount);
            
            _totalPaymentsProcessed += processedCount;
            _totalAmountProcessed += processedAmount;
            
            _logger.LogInformation("Batch processing completed: {Successful} successful, {Failed} failed, {Partial} partial payments", 
                successful, failed, partial);
            _logger.LogInformation("Session totals: {TotalPayments} payments, {TotalAmount:F8} ALPHA processed", 
                _totalPaymentsProcessed, _totalAmountProcessed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing payment batch");
            throw;
        }
    }

    public async Task<AutomationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var currentBlock = await _alphaRpcClient.GetBlockCountAsync(cancellationToken);
            var walletBalance = await _alphaRpcClient.GetBalanceAsync(cancellationToken);
            var pendingPayments = await _apiClient.GetPendingPaymentsAsync(_config.PoolId, cancellationToken);
            
            var nextProcessingBlock = _lastProcessedBlock + _config.Automation.BlockPeriod;
            var blocksUntilNext = Math.Max(0, nextProcessingBlock - currentBlock);
            
            string status;
            if (!_isRunning)
                status = "Stopped";
            else if (blocksUntilNext > 0)
                status = $"Waiting ({blocksUntilNext} blocks until next processing)";
            else if (pendingPayments.Count == 0)
                status = "No pending payments";
            else if (walletBalance < _config.Automation.MinimumBalance)
                status = "Insufficient balance";
            else
                status = "Ready to process";

            return new AutomationStatus
            {
                IsRunning = _isRunning,
                CurrentBlockHeight = currentBlock,
                LastProcessedBlock = _lastProcessedBlock,
                NextProcessingBlock = nextProcessingBlock,
                WalletBalance = walletBalance,
                PendingPaymentsCount = pendingPayments.Count,
                LastProcessingTime = _lastProcessingTime,
                Status = status,
                TotalPaymentsProcessed = _totalPaymentsProcessed,
                TotalAmountProcessed = _totalAmountProcessed,
                StartTime = _startTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting automation status");
            return new AutomationStatus
            {
                Status = $"Error: {ex.Message}"
            };
        }
    }

    private Table CreateStatusTable(AutomationStatus? status = null)
    {
        var table = new Table()
            .BorderColor(Color.Green)
            .Border(TableBorder.Rounded)
            .Title("[bold green]Automated Payment Processor Status[/]")
            .AddColumn("[bold]Property[/]")
            .AddColumn("[bold]Value[/]");

        if (status == null)
        {
            table.AddRow("Status", "[yellow]Loading...[/]");
            return table;
        }

        // Wallet Balance (highlighted at top)
        var balanceColor = status.WalletBalance >= _config.Automation.MinimumBalance ? "green" : "red";
        table.AddRow("[bold]Wallet Balance[/]", $"[{balanceColor}]{status.WalletBalance:F8} ALPHA[/]");
        
        table.AddRow("", ""); // Spacer
        
        // Configuration
        table.AddRow("Batch Size", _config.Automation.BatchSize.ToString());
        table.AddRow("Block Period", _config.Automation.BlockPeriod.ToString());
        table.AddRow("", ""); // Spacer
        
        // Current Status
        var statusColor = status.IsRunning ? "green" : "red";
        table.AddRow("Status", $"[{statusColor}]{status.Status}[/]");
        table.AddRow("Current Block", status.CurrentBlockHeight.ToString());
        table.AddRow("Last Processed Block", status.LastProcessedBlock.ToString());
        table.AddRow("Next Processing Block", status.NextProcessingBlock.ToString());
        table.AddRow("Pending Payments", status.PendingPaymentsCount.ToString());
        
        if (status.LastProcessingTime != DateTime.MinValue)
        {
            table.AddRow("Last Processing", status.LastProcessingTime.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        }
        
        table.AddRow("", ""); // Spacer
        
        // Session Statistics
        table.AddRow("[bold]Total Payments Processed[/]", $"[green]{status.TotalPaymentsProcessed}[/]");
        table.AddRow("[bold]Total Amount Processed[/]", $"[green]{status.TotalAmountProcessed:F8} ALPHA[/]");
        
        if (status.StartTime != DateTime.MinValue)
        {
            var uptime = DateTime.UtcNow - status.StartTime;
            table.AddRow("Session Uptime", $"{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}");
        }

        return table;
    }
}
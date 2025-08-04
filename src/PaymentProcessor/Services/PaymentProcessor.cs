using Microsoft.Extensions.Logging;
using PaymentProcessor.Configuration;
using PaymentProcessor.Models;
using Spectre.Console;

namespace PaymentProcessor.Services;

public class PaymentProcessor : IPaymentProcessor
{
    private readonly PaymentProcessorConfig _config;
    private readonly ILogger<PaymentProcessor> _logger;
    private readonly IAlphaPaymentService _alphaPaymentService;

    public PaymentProcessor(
        PaymentProcessorConfig config, 
        ILogger<PaymentProcessor> logger,
        IAlphaPaymentService alphaPaymentService)
    {
        _config = config;
        _logger = logger;
        _alphaPaymentService = alphaPaymentService;
    }

    public async Task<List<PaymentProcessingResult>> ProcessPaymentsAsync(List<PendingPayment> payments, CancellationToken cancellationToken = default)
    {
        return await ProcessPaymentsAsync(payments, showProgress: true, cancellationToken);
    }

    public async Task<List<PaymentProcessingResult>> ProcessPaymentsAsync(List<PendingPayment> payments, bool showProgress, CancellationToken cancellationToken = default)
    {
        var results = new List<PaymentProcessingResult>();

        _logger.LogInformation("Processing {Count} payments", payments.Count);

        if (showProgress)
        {
            // Interactive progress display for manual mode
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new ProgressColumn[] 
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Processing payments[/]");
                    task.MaxValue = payments.Count;

                    foreach (var payment in payments)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        task.Description = $"[green]Processing payment for {payment.Address}[/]";
                        
                        await ProcessSinglePaymentAsync(payment, results, cancellationToken);
                        
                        task.Increment(1);
                        
                        // Small delay to show progress
                        await Task.Delay(100, cancellationToken);
                    }
                });
        }
        else
        {
            // Simple processing without interactive progress display for automation mode
            for (int i = 0; i < payments.Count; i++)
            {
                var payment = payments[i];
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation("Processing payment {Current}/{Total} for {Address}: {Amount} ALPHA", 
                    i + 1, payments.Count, payment.Address, payment.Amount);
                
                await ProcessSinglePaymentAsync(payment, results, cancellationToken);
            }
        }

        return results;
    }

    private async Task ProcessSinglePaymentAsync(PendingPayment payment, List<PaymentProcessingResult> results, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _alphaPaymentService.ProcessPaymentAsync(payment, cancellationToken);
            results.Add(result);
            
            if (result.Success)
            {
                _logger.LogInformation("Successfully processed payment for {Address}: {Amount}", 
                    payment.Address, payment.Amount);
            }
            else if (result.IsPartialPayment)
            {
                _logger.LogWarning("Partial payment for {Address}: {Completed}/{Total} ALPHA - {Error}", 
                    payment.Address, result.CompletedAmount, payment.Amount, result.Error);
            }
            else
            {
                _logger.LogError("Payment failed for {Address}: {Error} - STOPPING PROCESSING", 
                    payment.Address, result.Error);
                throw new InvalidOperationException($"Payment processing failed for {payment.Address}: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment processing stopped due to error for {Address}", payment.Address);
            
            // Add failed result for current payment if not already added
            if (!results.Any(r => r.Address == payment.Address))
            {
                results.Add(new PaymentProcessingResult
                {
                    Address = payment.Address,
                    Amount = payment.Amount,
                    Success = false,
                    Error = ex.Message
                });
            }
            
            // Stop processing and re-throw
            throw;
        }
    }
}
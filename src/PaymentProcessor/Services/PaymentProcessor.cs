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
        var results = new List<PaymentProcessingResult>();

        _logger.LogInformation("Processing {Count} payments", payments.Count);

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
                    
                    try
                    {
                        var result = await _alphaPaymentService.ProcessPaymentAsync(payment, cancellationToken);
                        results.Add(result);
                        
                        if (result.Success)
                        {
                            _logger.LogInformation("Successfully processed payment for {Address}: {Amount}", 
                                payment.Address, payment.Amount);
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
                    
                    task.Increment(1);
                    
                    // Small delay to show progress
                    await Task.Delay(100, cancellationToken);
                }
            });

        return results;
    }
}
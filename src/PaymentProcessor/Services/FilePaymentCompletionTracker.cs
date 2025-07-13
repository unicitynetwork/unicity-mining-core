using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PaymentProcessor.Services;

/// <summary>
/// Simple file-based payment completion tracker
/// </summary>
public class FilePaymentCompletionTracker : IPaymentCompletionTracker
{
    private readonly ILogger<FilePaymentCompletionTracker> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    
    public FilePaymentCompletionTracker(ILogger<FilePaymentCompletionTracker> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? Path.Combine(Directory.GetCurrentDirectory(), "completed_payments.json");
    }
    
    public async Task<bool> IsPaymentCompletedAsync(long paymentId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var completedPayments = await LoadCompletedPaymentsAsync();
            return completedPayments.ContainsKey(paymentId);
        }
        finally
        {
            _fileLock.Release();
        }
    }
    
    public async Task MarkPaymentCompletedAsync(long paymentId, string transactionId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var completedPayments = await LoadCompletedPaymentsAsync();
            completedPayments[paymentId] = new CompletedPaymentRecord
            {
                PaymentId = paymentId,
                TransactionId = transactionId,
                CompletedAt = DateTime.UtcNow
            };
            
            await SaveCompletedPaymentsAsync(completedPayments);
            _logger.LogInformation("Marked payment {PaymentId} as completed with transaction {TransactionId}", paymentId, transactionId);
        }
        finally
        {
            _fileLock.Release();
        }
    }
    
    public async Task<string?> GetPaymentTransactionIdAsync(long paymentId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var completedPayments = await LoadCompletedPaymentsAsync();
            return completedPayments.TryGetValue(paymentId, out var record) ? record.TransactionId : null;
        }
        finally
        {
            _fileLock.Release();
        }
    }
    
    private async Task<Dictionary<long, CompletedPaymentRecord>> LoadCompletedPaymentsAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<long, CompletedPaymentRecord>();
        }
        
        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var records = JsonSerializer.Deserialize<CompletedPaymentRecord[]>(json) ?? Array.Empty<CompletedPaymentRecord>();
            return records.ToDictionary(r => r.PaymentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load completed payments from {FilePath}", _filePath);
            return new Dictionary<long, CompletedPaymentRecord>();
        }
    }
    
    private async Task SaveCompletedPaymentsAsync(Dictionary<long, CompletedPaymentRecord> completedPayments)
    {
        try
        {
            var records = completedPayments.Values.ToArray();
            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save completed payments to {FilePath}", _filePath);
            throw;
        }
    }
}

public record CompletedPaymentRecord
{
    public long PaymentId { get; init; }
    public string TransactionId { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; }
}
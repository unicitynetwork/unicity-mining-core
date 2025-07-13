namespace PaymentProcessor.Services;

/// <summary>
/// Service for tracking completed payments to prevent double payments
/// </summary>
public interface IPaymentCompletionTracker
{
    /// <summary>
    /// Check if a payment has already been completed
    /// </summary>
    Task<bool> IsPaymentCompletedAsync(long paymentId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Mark a payment as completed
    /// </summary>
    Task MarkPaymentCompletedAsync(long paymentId, string transactionId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get the transaction ID for a completed payment
    /// </summary>
    Task<string?> GetPaymentTransactionIdAsync(long paymentId, CancellationToken cancellationToken = default);
}
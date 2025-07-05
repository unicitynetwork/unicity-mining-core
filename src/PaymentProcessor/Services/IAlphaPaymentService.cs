using PaymentProcessor.Models;

namespace PaymentProcessor.Services;

public interface IAlphaPaymentService
{
    Task<PaymentProcessingResult> ProcessPaymentAsync(PendingPayment payment, CancellationToken cancellationToken = default);
    Task<List<PaymentProcessingResult>> ProcessPaymentsBatchAsync(List<PendingPayment> payments, CancellationToken cancellationToken = default);
    Task<decimal> GetAvailableBalanceAsync(CancellationToken cancellationToken = default);
    Task<bool> ValidatePaymentsAsync(List<PendingPayment> payments, CancellationToken cancellationToken = default);
    Task<decimal> EstimateTransactionFeeAsync(List<PendingPayment> payments, CancellationToken cancellationToken = default);
}
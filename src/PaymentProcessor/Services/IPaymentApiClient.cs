using PaymentProcessor.Models;

namespace PaymentProcessor.Services;

public interface IPaymentApiClient
{
    Task<List<PendingPayment>> GetPendingPaymentsAsync(string poolId, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<bool> MarkPaymentCompletedAsync(string poolId, PendingPayment payment, string transactionId, CancellationToken cancellationToken = default);
}
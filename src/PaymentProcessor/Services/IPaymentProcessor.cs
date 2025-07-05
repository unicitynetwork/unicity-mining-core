using PaymentProcessor.Models;

namespace PaymentProcessor.Services;

public interface IPaymentProcessor
{
    Task<List<PaymentProcessingResult>> ProcessPaymentsAsync(List<PendingPayment> payments, CancellationToken cancellationToken = default);
}
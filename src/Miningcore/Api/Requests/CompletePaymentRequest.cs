namespace Miningcore.Api.Requests;

public class CompletePaymentRequest
{
    public long PaymentId { get; set; }
    public string TransactionId { get; set; }
}
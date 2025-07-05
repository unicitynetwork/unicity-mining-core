namespace Miningcore.Api.Requests;

public class CompletePaymentRequest
{
    public string Address { get; set; }
    public decimal Amount { get; set; }
    public string TransactionId { get; set; }
}
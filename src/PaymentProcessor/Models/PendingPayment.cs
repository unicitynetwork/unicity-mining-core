namespace PaymentProcessor.Models;

public record PendingPayment
{
    public long Id { get; init; }
    public string Address { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public record PendingPaymentsResponse
{
    public string PoolId { get; init; } = string.Empty;
    public List<PendingPayment> Payments { get; init; } = new();
}

public record PaymentProcessingResult
{
    public string Address { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? TransactionId { get; init; }
}
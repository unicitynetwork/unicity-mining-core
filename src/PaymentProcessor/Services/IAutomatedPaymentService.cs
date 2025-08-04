using PaymentProcessor.Models;

namespace PaymentProcessor.Services;

public interface IAutomatedPaymentService
{
    Task RunAutomatedPaymentsAsync(CancellationToken cancellationToken = default);
    Task<AutomationStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public record AutomationStatus
{
    public bool IsRunning { get; init; }
    public int CurrentBlockHeight { get; init; }
    public int LastProcessedBlock { get; init; }
    public int NextProcessingBlock { get; init; }
    public decimal WalletBalance { get; init; }
    public int PendingPaymentsCount { get; init; }
    public DateTime LastProcessingTime { get; init; }
    public string Status { get; init; } = string.Empty;
    public int TotalPaymentsProcessed { get; init; }
    public decimal TotalAmountProcessed { get; init; }
    public DateTime StartTime { get; init; }
}
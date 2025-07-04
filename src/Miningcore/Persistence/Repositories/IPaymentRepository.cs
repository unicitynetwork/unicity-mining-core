using System.Data;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;

namespace Miningcore.Persistence.Repositories;

public interface IPaymentRepository
{
    Task InsertAsync(IDbConnection con, IDbTransaction tx, Payment payment);
    Task BatchInsertAsync(IDbConnection con, IDbTransaction tx, IEnumerable<Payment> shares);

    Task<Payment[]> PagePaymentsAsync(IDbConnection con, string poolId, string address, int page, int pageSize, CancellationToken ct);
    Task<BalanceChange[]> PageBalanceChangesAsync(IDbConnection con, string poolId, string address, int page, int pageSize, CancellationToken ct);
    Task<AmountByDate[]> PageMinerPaymentsByDayAsync(IDbConnection con, string poolId, string address, int page, int pageSize, CancellationToken ct);
    Task<uint> GetPaymentsCountAsync(IDbConnection con, string poolId, string address, CancellationToken ct);
    Task<uint> GetMinerPaymentsByDayCountAsync(IDbConnection con, string poolId, string address);
    Task<uint> GetBalanceChangesCountAsync(IDbConnection con, string poolId, string address = null);

    /// <summary>
    /// Returns pending payments (empty transaction confirmation data) for a specific pool
    /// </summary>
    Task<Payment[]> GetPendingPaymentsAsync(IDbConnection con, string poolId);

    /// <summary>
    /// Marks a payment as completed by updating transaction confirmation data
    /// </summary>
    Task<bool> CompletePaymentAsync(IDbConnection con, IDbTransaction tx, string poolId, long paymentId, string transactionId);
}

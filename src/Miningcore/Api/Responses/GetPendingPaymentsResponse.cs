using System;

namespace Miningcore.Api.Responses;

public class GetPendingPaymentsResponse
{
    public string PoolId { get; set; }
    public PendingPayment[] Payments { get; set; }
}

public class PendingPayment
{
    public long Id { get; set; }
    public string Address { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedUtc { get; set; }
}
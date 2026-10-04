namespace EsimBot.BLL.DTO;

public sealed record OrderStatistics(
    long Buyers, long TotalOrders, long OrdersLast24Hours, long Quoted, long Paid,
    long Provisioning, long Ready, long Delivered, long RefundPending, long Refunded, long ManualReview);

public sealed record PaymentStatistics(
    long TotalPayments, long SuccessfulPayments, long SuccessfulLast24Hours, long Accepted,
    long RefundPending, long Refunded, long GrossStars, long RefundedStars);

public sealed record AdminStatistics(
    long Users,
    OrderStatistics Orders,
    PaymentStatistics Payments,
    long IssuedEsims,
    long? ProviderBalanceUnits,
    bool BotReady,
    DateTime GeneratedAtUtc)
{
    public long NetStars => Math.Max(0, Payments.GrossStars - Payments.RefundedStars);
}

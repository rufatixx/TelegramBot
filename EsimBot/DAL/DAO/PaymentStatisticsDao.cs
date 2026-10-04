namespace EsimBot.DAL.DAO;

public sealed record PaymentStatisticsDao(
    long TotalPayments,
    long SuccessfulPayments,
    long SuccessfulLast24Hours,
    long Accepted,
    long RefundPending,
    long Refunded,
    long GrossStars,
    long RefundedStars);

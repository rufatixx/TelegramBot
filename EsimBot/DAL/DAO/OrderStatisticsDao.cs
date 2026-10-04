namespace EsimBot.DAL.DAO;

public sealed record OrderStatisticsDao(
    long Buyers,
    long TotalOrders,
    long OrdersLast24Hours,
    long Quoted,
    long Paid,
    long Provisioning,
    long Ready,
    long Delivered,
    long RefundPending,
    long Refunded,
    long ManualReview);

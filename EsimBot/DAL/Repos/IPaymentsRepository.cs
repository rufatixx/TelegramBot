using EsimBot.DAL.DAO;

namespace EsimBot.DAL.Repos;

public interface IPaymentsRepository
{
    Task<PaymentStatisticsDao> GetStatisticsAsync(IDatabaseSession session, CancellationToken ct) => throw new NotSupportedException();
    Task<PaymentDao?> GetAsync(IDatabaseSession session, string chargeId, bool forUpdate, CancellationToken ct);
    Task<PaymentDao?> FindPrimaryForOrderAsync(IDatabaseSession session, string orderId, CancellationToken ct);
    Task InsertAsync(IDatabaseSession session, PaymentDao payment, CancellationToken ct);
    Task QueuePrimaryRefundAsync(IDatabaseSession session, string orderId, CancellationToken ct);
    Task<IReadOnlyList<PaymentDao>> DueRefundsAsync(IDatabaseSession session, CancellationToken ct);
    Task MarkRefundedAsync(IDatabaseSession session, string chargeId, CancellationToken ct);
    Task RetryRefundAsync(IDatabaseSession session, string chargeId, CancellationToken ct);
}

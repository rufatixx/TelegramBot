using EsimBot.DAL.DAO;

namespace EsimBot.DAL.Repos;

public interface IOrdersRepository
{
    Task<OrderStatisticsDao> GetStatisticsAsync(IDatabaseSession session, CancellationToken ct) => throw new NotSupportedException();
    Task InsertQuoteAsync(IDatabaseSession session, OrderQuoteDao quote, CancellationToken ct);
    Task<OrderDao?> FindByRequestKeyAsync(IDatabaseSession session, string requestKey, CancellationToken ct);
    Task<OrderDao?> GetAsync(IDatabaseSession session, string id, long? userId, bool forUpdate, CancellationToken ct);
    Task<IReadOnlyList<OrderDao>> ListAsync(IDatabaseSession session, long userId, int offset, CancellationToken ct);
    Task<IReadOnlyList<OrderDao>> DueAsync(IDatabaseSession session, CancellationToken ct);
    Task SetPaidAsync(IDatabaseSession session, string id, CancellationToken ct);
    Task<bool> BeginProvisionAsync(IDatabaseSession session, string id, CancellationToken ct);
    Task<bool> SaveProviderOrderAsync(IDatabaseSession session, string id, string number, CancellationToken ct);
    Task MarkReadyAsync(IDatabaseSession session, string id, CancellationToken ct);
    Task MarkDeliveredAsync(IDatabaseSession session, string id, CancellationToken ct);
    Task RetryAsync(IDatabaseSession session, string id, string code, int delaySeconds, bool manualReview, CancellationToken ct);
    Task QueueRefundAsync(IDatabaseSession session, string id, CancellationToken ct);
    Task MarkRefundedAsync(IDatabaseSession session, string id, bool onlyPending, CancellationToken ct);
    Task ResumeProvisionAsync(IDatabaseSession session, string id, CancellationToken ct);
}

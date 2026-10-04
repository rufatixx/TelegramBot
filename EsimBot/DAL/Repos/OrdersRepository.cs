using Dapper;
using EsimBot.DAL.DAO;

namespace EsimBot.DAL.Repos;

public sealed class OrdersRepository : IOrdersRepository
{
    private const string Columns = "id AS Id,user_id AS UserId,request_key AS RequestKey,package_code AS PackageCode,package_name AS PackageName,cost_units AS CostUnits,stars AS Stars,state AS State,provider_order_no AS ProviderOrderNumber,terms_version AS TermsVersion,created_at AS CreatedAt,expires_at AS ExpiresAt,updated_at AS UpdatedAt,attempts AS Attempts,next_attempt_at AS NextAttemptAt,last_error_code AS LastErrorCode,delivered_at AS DeliveredAt";

    public Task<OrderStatisticsDao> GetStatisticsAsync(IDatabaseSession session, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.QuerySingleAsync<OrderStatisticsDao>(new CommandDefinition("""
            SELECT COUNT(DISTINCT user_id) AS Buyers,
                   COUNT(*) AS TotalOrders,
                   CAST(COALESCE(SUM(created_at >= UTC_TIMESTAMP(6) - INTERVAL 24 HOUR),0) AS SIGNED) AS OrdersLast24Hours,
                   CAST(COALESCE(SUM(state='quoted'),0) AS SIGNED) AS Quoted,
                   CAST(COALESCE(SUM(state='paid'),0) AS SIGNED) AS Paid,
                   CAST(COALESCE(SUM(state='provisioning'),0) AS SIGNED) AS Provisioning,
                   CAST(COALESCE(SUM(state='ready'),0) AS SIGNED) AS Ready,
                   CAST(COALESCE(SUM(delivered_at IS NOT NULL),0) AS SIGNED) AS Delivered,
                   CAST(COALESCE(SUM(state='refund_pending'),0) AS SIGNED) AS RefundPending,
                   CAST(COALESCE(SUM(state='refunded'),0) AS SIGNED) AS Refunded,
                   CAST(COALESCE(SUM(state='manual_review'),0) AS SIGNED) AS ManualReview
            FROM orders
            """, transaction: db.Transaction, cancellationToken: ct));
    }

    public Task InsertQuoteAsync(IDatabaseSession session, OrderQuoteDao quote, CancellationToken ct)
        => ExecuteAsync(session, """
            INSERT INTO orders (id,user_id,request_key,package_code,package_name,cost_units,stars,terms_version,expires_at)
            VALUES (@Id,@UserId,@RequestKey,@PackageCode,@PackageName,@CostUnits,@Stars,@TermsVersion,@ExpiresAt)
            ON DUPLICATE KEY UPDATE id=id
            """, quote, ct);

    public Task<OrderDao?> FindByRequestKeyAsync(IDatabaseSession session, string requestKey, CancellationToken ct)
        => QueryOneAsync(session, $"SELECT {Columns} FROM orders WHERE request_key=@RequestKey", new { RequestKey = requestKey }, ct);

    public Task<OrderDao?> GetAsync(IDatabaseSession session, string id, long? userId, bool forUpdate, CancellationToken ct)
        => QueryOneAsync(session, $"SELECT {Columns} FROM orders WHERE id=@Id AND (@UserId IS NULL OR user_id=@UserId)"
            + (forUpdate ? " FOR UPDATE" : ""), new { Id = id, UserId = userId }, ct);

    public Task<IReadOnlyList<OrderDao>> ListAsync(IDatabaseSession session, long userId, int offset, CancellationToken ct)
        => QueryManyAsync(session, $"SELECT {Columns} FROM orders WHERE user_id=@UserId ORDER BY created_at DESC,id DESC LIMIT 8 OFFSET @Offset",
            new { UserId = userId, Offset = offset }, ct);

    public Task<IReadOnlyList<OrderDao>> DueAsync(IDatabaseSession session, CancellationToken ct)
        => QueryManyAsync(session, $"SELECT {Columns} FROM orders WHERE (state IN ('paid','provisioning') OR (state='ready' AND delivered_at IS NULL)) AND next_attempt_at<=UTC_TIMESTAMP(6) ORDER BY next_attempt_at LIMIT 10", null, ct);

    public Task SetPaidAsync(IDatabaseSession session, string id, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE orders SET state='paid',next_attempt_at=UTC_TIMESTAMP(6) WHERE id=@Id", new { Id = id }, ct);

    public async Task<bool> BeginProvisionAsync(IDatabaseSession session, string id, CancellationToken ct)
        => await ExecuteAsync(session, "UPDATE orders SET state='provisioning' WHERE id=@Id AND state='paid'", new { Id = id }, ct) == 1;

    public async Task<bool> SaveProviderOrderAsync(IDatabaseSession session, string id, string number, CancellationToken ct)
        => await ExecuteAsync(session, "UPDATE orders SET provider_order_no=@Number WHERE id=@Id AND state='provisioning' AND (provider_order_no IS NULL OR provider_order_no=@Number)", new { Id = id, Number = number }, ct) == 1;

    public Task MarkReadyAsync(IDatabaseSession session, string id, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE orders SET state='ready',attempts=0,next_attempt_at=UTC_TIMESTAMP(6),last_error_code=NULL WHERE id=@Id", new { Id = id }, ct);

    public Task MarkDeliveredAsync(IDatabaseSession session, string id, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE orders SET delivered_at=UTC_TIMESTAMP(6),last_error_code=NULL WHERE id=@Id AND state='ready'", new { Id = id }, ct);

    public Task RetryAsync(IDatabaseSession session, string id, string code, int delaySeconds, bool manualReview, CancellationToken ct)
        => ExecuteAsync(session, """
            UPDATE orders SET attempts=attempts+IF(@Code='supplier_balance_low',0,1),last_error_code=@Code,next_attempt_at=TIMESTAMPADD(SECOND,@Delay,UTC_TIMESTAMP(6)),
            state=IF(@ManualReview AND state='provisioning','manual_review',state) WHERE id=@Id
            """, new { Id = id, Code = code, Delay = delaySeconds, ManualReview = manualReview }, ct);

    public Task QueueRefundAsync(IDatabaseSession session, string id, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE orders SET state='refund_pending' WHERE id=@Id", new { Id = id }, ct);

    public Task MarkRefundedAsync(IDatabaseSession session, string id, bool onlyPending, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE orders SET state='refunded' WHERE id=@Id AND (@OnlyPending=FALSE OR state='refund_pending')", new { Id = id, OnlyPending = onlyPending }, ct);

    public Task ResumeProvisionAsync(IDatabaseSession session, string id, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE orders SET state='provisioning',attempts=0,next_attempt_at=UTC_TIMESTAMP(6),last_error_code=NULL WHERE id=@Id AND state='manual_review'", new { Id = id }, ct);

    private static Task<int> ExecuteAsync(IDatabaseSession session, string sql, object? parameters, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.ExecuteAsync(new CommandDefinition(sql, parameters, db.Transaction, cancellationToken: ct));
    }

    private static Task<OrderDao?> QueryOneAsync(IDatabaseSession session, string sql, object parameters, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.QuerySingleOrDefaultAsync<OrderDao>(new CommandDefinition(sql, parameters, db.Transaction, cancellationToken: ct));
    }

    private static async Task<IReadOnlyList<OrderDao>> QueryManyAsync(IDatabaseSession session, string sql, object? parameters, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return (await db.Connection.QueryAsync<OrderDao>(new CommandDefinition(sql, parameters, db.Transaction, cancellationToken: ct))).AsList();
    }
}

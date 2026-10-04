using Dapper;
using EsimBot.DAL.DAO;

namespace EsimBot.DAL.Repos;

public sealed class PaymentsRepository : IPaymentsRepository
{
    private const string Columns = "charge_id AS ChargeId,order_id AS OrderId,payer_id AS PayerId,stars AS Stars,state AS State,accepted_at AS AcceptedAt,created_at AS CreatedAt,refunded_at AS RefundedAt,attempts AS Attempts,next_attempt_at AS NextAttemptAt";

    public Task<PaymentStatisticsDao> GetStatisticsAsync(IDatabaseSession session, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.QuerySingleAsync<PaymentStatisticsDao>(new CommandDefinition("""
            SELECT COUNT(*) AS TotalPayments,
                   CAST(COALESCE(SUM(accepted_at IS NOT NULL),0) AS SIGNED) AS SuccessfulPayments,
                   CAST(COALESCE(SUM(accepted_at >= UTC_TIMESTAMP(6) - INTERVAL 24 HOUR),0) AS SIGNED) AS SuccessfulLast24Hours,
                   CAST(COALESCE(SUM(state='accepted'),0) AS SIGNED) AS Accepted,
                   CAST(COALESCE(SUM(state='refund_pending'),0) AS SIGNED) AS RefundPending,
                   CAST(COALESCE(SUM(state='refunded'),0) AS SIGNED) AS Refunded,
                   CAST(COALESCE(SUM(CASE WHEN accepted_at IS NOT NULL THEN stars ELSE 0 END),0) AS SIGNED) AS GrossStars,
                   CAST(COALESCE(SUM(CASE WHEN accepted_at IS NOT NULL AND refunded_at IS NOT NULL THEN stars ELSE 0 END),0) AS SIGNED) AS RefundedStars
            FROM payments
            """, transaction: db.Transaction, cancellationToken: ct));
    }

    public Task<PaymentDao?> GetAsync(IDatabaseSession session, string chargeId, bool forUpdate, CancellationToken ct)
        => QueryOneAsync(session, $"SELECT {Columns} FROM payments WHERE charge_id=@ChargeId" + (forUpdate ? " FOR UPDATE" : ""), new { ChargeId = chargeId }, ct);

    public Task<PaymentDao?> FindPrimaryForOrderAsync(IDatabaseSession session, string orderId, CancellationToken ct)
        => QueryOneAsync(session, $"SELECT {Columns} FROM payments WHERE order_id=@OrderId AND accepted_at IS NOT NULL", new { OrderId = orderId }, ct);

    public Task InsertAsync(IDatabaseSession session, PaymentDao payment, CancellationToken ct)
        => ExecuteAsync(session, """
            INSERT INTO payments (charge_id,order_id,payer_id,stars,state,accepted_at)
            VALUES (@ChargeId,@OrderId,@PayerId,@Stars,@State,@AcceptedAt)
            """, payment, ct);

    public Task QueuePrimaryRefundAsync(IDatabaseSession session, string orderId, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE payments SET state='refund_pending',next_attempt_at=UTC_TIMESTAMP(6) WHERE order_id=@OrderId AND state='accepted'", new { OrderId = orderId }, ct);

    public async Task<IReadOnlyList<PaymentDao>> DueRefundsAsync(IDatabaseSession session, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return (await db.Connection.QueryAsync<PaymentDao>(new CommandDefinition(
            $"SELECT {Columns} FROM payments WHERE state='refund_pending' AND next_attempt_at<=UTC_TIMESTAMP(6) ORDER BY next_attempt_at LIMIT 10",
            transaction: db.Transaction, cancellationToken: ct))).AsList();
    }

    public Task MarkRefundedAsync(IDatabaseSession session, string chargeId, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE payments SET state='refunded',refunded_at=COALESCE(refunded_at,UTC_TIMESTAMP(6)) WHERE charge_id=@ChargeId", new { ChargeId = chargeId }, ct);

    public Task RetryRefundAsync(IDatabaseSession session, string chargeId, CancellationToken ct)
        => ExecuteAsync(session, "UPDATE payments SET attempts=attempts+1,next_attempt_at=UTC_TIMESTAMP(6)+INTERVAL 5 MINUTE WHERE charge_id=@ChargeId", new { ChargeId = chargeId }, ct);

    private static Task<PaymentDao?> QueryOneAsync(IDatabaseSession session, string sql, object parameters, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.QuerySingleOrDefaultAsync<PaymentDao>(new CommandDefinition(sql, parameters, db.Transaction, cancellationToken: ct));
    }

    private static Task<int> ExecuteAsync(IDatabaseSession session, string sql, object parameters, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.ExecuteAsync(new CommandDefinition(sql, parameters, db.Transaction, cancellationToken: ct));
    }
}

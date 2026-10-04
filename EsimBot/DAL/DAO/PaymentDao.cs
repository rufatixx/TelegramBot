namespace EsimBot.DAL.DAO;

/// <summary>Payment ledger row. AcceptedAt retains the original purchase association after a refund.</summary>
public sealed record PaymentDao(
    string ChargeId,
    string? OrderId,
    long PayerId,
    int Stars,
    string State,
    DateTime? AcceptedAt,
    DateTime CreatedAt,
    DateTime? RefundedAt,
    int Attempts,
    DateTime NextAttemptAt);

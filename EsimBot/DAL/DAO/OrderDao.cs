namespace EsimBot.DAL.DAO;

/// <summary>Persisted order snapshot and processing metadata; never sent directly to API clients.</summary>
public sealed record OrderDao(
    string Id,
    long UserId,
    string RequestKey,
    string PackageCode,
    string PackageName,
    long CostUnits,
    int Stars,
    string State,
    string? ProviderOrderNumber,
    string TermsVersion,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime UpdatedAt,
    int Attempts,
    DateTime NextAttemptAt,
    string? LastErrorCode,
    DateTime? DeliveredAt);

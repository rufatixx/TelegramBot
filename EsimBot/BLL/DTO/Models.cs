namespace EsimBot.BLL.DTO;

public sealed record Country(string Code, string Name);

public sealed record EsimPackage(
    string Code, string Name, long PriceUnits, string Currency, long VolumeBytes,
    int Duration, string DurationUnit, IReadOnlyList<Country> Countries,
    string Description, string ActivationPolicy, string Speed, string DataType)
{
    public decimal PriceUsd => PriceUnits / 10000m;
}

public sealed record ProviderProfile(
    string Id, string Iccid, string ActivationCode, string? Apn, string Status);

public sealed record ProviderOrder(string OrderNumber, IReadOnlyList<ProviderProfile> Profiles);
public sealed record EsimUsage(long UsedBytes, long TotalBytes, string Status, DateTime? ExpiresAt);

public sealed class ProviderException(string code, bool retryable) : Exception($"Provider error: {code}")
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed class ShopException(string message) : Exception(message);
public sealed record ShopReadiness(bool CanBrowse, bool CanBuy);

public sealed record Order(
    string Id, long UserId, string PackageCode, string PackageName, long CostUnits,
    int Stars, string State, string? ProviderOrderNumber, DateTime CreatedAt,
    DateTime ExpiresAt, int Attempts, DateTime? DeliveredAt);

public sealed record StoredProfile(string Id, string Iccid, string ActivationCode, string? Apn);
public sealed record Payment(string ChargeId, string? OrderId, long UserId, int Stars, string State, DateTime? AcceptedAt);
public sealed record PaymentResult(bool Accepted, bool Duplicate, bool RefundQueued);

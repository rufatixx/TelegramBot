using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface IEsimAccessClient
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct);
    Task<long> GetBalanceUnitsAsync(CancellationToken ct);
    Task<string> PlaceOrderAsync(string transactionId, string packageCode, long priceUnits, CancellationToken ct);
    // Returns null while allocation is pending or this transaction is not yet visible.
    Task<ProviderOrder?> QueryOrderAsync(string transactionId, string? orderNumber, CancellationToken ct);
    Task<EsimUsage> GetUsageAsync(string esimId, CancellationToken ct);
}

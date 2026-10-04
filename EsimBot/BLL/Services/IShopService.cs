using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface IShopService
{
    ShopReadiness Readiness { get; }
    Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct);
    // Called only after the customer explicitly accepts TermsVersion and device compatibility.
    Task<Order> QuoteAsync(long userId, string packageCode, string requestKey, CancellationToken ct);
    Task<string?> ValidateCheckoutAsync(long userId, string orderId, string currency, int stars, CancellationToken ct);
    Task<PaymentResult> RecordPaymentAsync(long userId, string orderId, string chargeId, string currency, int stars, CancellationToken ct);
    Task RecordRefundAsync(long userId, string chargeId, CancellationToken ct);
    Task<IReadOnlyList<Order>> GetOrdersAsync(long userId, int page, CancellationToken ct);
    Task<Order?> GetOrderAsync(long userId, string orderId, CancellationToken ct);
    Task<StoredProfile?> GetProfileAsync(long userId, string orderId, CancellationToken ct);
    Task<EsimUsage?> GetUsageAsync(long userId, string orderId, CancellationToken ct);
    Task<bool> RequestRefundAsync(long adminUserId, string orderId, CancellationToken ct);
    Task<bool> RequestRetryAsync(long adminUserId, string orderId, CancellationToken ct);
}

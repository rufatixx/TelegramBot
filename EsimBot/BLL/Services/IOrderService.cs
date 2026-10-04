using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface IOrderService
{
    Task<Order> CreateQuoteAsync(long userId, EsimPackage package, int stars, string termsVersion, string requestKey, CancellationToken ct);
    Task<Order?> GetAsync(string id, long? userId, CancellationToken ct);
    Task<IReadOnlyList<Order>> ListAsync(long userId, int page, CancellationToken ct);
    Task<PaymentResult> RecordPaymentAsync(long payerId, string orderId, string chargeId, int stars, CancellationToken ct);
    Task<StoredProfile?> GetProfileAsync(string orderId, long userId, CancellationToken ct);
    Task<IReadOnlyList<Order>> DueOrdersAsync(CancellationToken ct);
    Task<bool> BeginProvisionAsync(string id, CancellationToken ct);
    Task SaveProviderOrderAsync(string id, string orderNumber, CancellationToken ct);
    Task SaveProfileAsync(string orderId, ProviderProfile profile, CancellationToken ct);
    Task MarkDeliveredAsync(string id, CancellationToken ct);
    Task RetryOrderAsync(string id, string code, int delaySeconds, bool manualReview, CancellationToken ct);
    Task<bool> QueueUnsubmittedRefundAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<Payment>> DueRefundsAsync(CancellationToken ct);
    Task CompleteRefundAsync(Payment payment, CancellationToken ct);
    Task RetryRefundAsync(string chargeId, CancellationToken ct);
    Task<bool> ResumeProvisionAsync(string id, CancellationToken ct);
    Task<string?> RecordExternalRefundAsync(long userId, string chargeId, CancellationToken ct);
}

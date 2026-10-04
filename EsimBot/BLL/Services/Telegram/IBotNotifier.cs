using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface IBotNotifier
{
    Task DeliverAsync(Order order, StoredProfile profile, CancellationToken ct);
    Task RefundedAsync(Order order, CancellationToken ct);
    Task AlertAdminAsync(string orderId, string code, CancellationToken ct);
}

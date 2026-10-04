using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface ITelegramWebhookService
{
    Task<ApiResponse> ReceiveAsync(HttpContext context, CancellationToken ct);
}

using Telegram.Bot.Types;

namespace EsimBot.BLL.Services;

public interface ISupportService
{
    Task BeginAsync(long userId, bool payment, CancellationToken ct);
    Task<bool> HandleAsync(Message message, CancellationToken ct);
    Task CancelAsync(long userId, CancellationToken ct);
}

using Telegram.Bot.Types;

namespace EsimBot.BLL.Services;

public interface IBotHandler
{
    Task HandleAsync(Update update, CancellationToken ct);
    Task NotifyBusyAsync(Update update, CancellationToken ct) => Task.CompletedTask;
}

using EsimBot.BLL.DTO;
using Telegram.Bot.Types;

namespace EsimBot.BLL.Services;

public interface IUpdateDispatcher
{
    Task RunAsync(CancellationToken ct);
    bool TryQueueInteractive(Update update);
    Task<bool> HandlePriorityAsync(Update update, CancellationToken ct);
    Task DispatchPollingBatchAsync(IReadOnlyCollection<Update> updates, CancellationToken ct);
    UpdateDispatcherSnapshot GetSnapshot();
}

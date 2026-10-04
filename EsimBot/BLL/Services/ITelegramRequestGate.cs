namespace EsimBot.BLL.Services;

public interface ITelegramRequestGate
{
    ValueTask<IDisposable> AcquireAsync(long chatId, CancellationToken ct);
}

using Telegram.Bot.Types;

namespace EsimBot.BLL.Services;

public interface IPaymentTestService
{
    Task HandleAsync(Update update, CancellationToken ct);
}

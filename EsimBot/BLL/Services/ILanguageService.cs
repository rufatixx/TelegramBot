using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface ILanguageService
{
    Task<BotText> GetAsync(long userId, string? telegramLanguageCode, CancellationToken ct);
    Task SetAsync(long userId, string language, CancellationToken ct);
}

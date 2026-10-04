using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EsimBot.BLL.Services;

public interface IChatUiService
{
    IDisposable BeginScreen(int? messageId);
    Task ShowAsync(long userId, string text, InlineKeyboardMarkup? keyboard, ParseMode parseMode, CancellationToken ct);
    Task ClearAsync(long userId, CancellationToken ct);
    Task TrackAsync(long userId, int messageId, bool replace, CancellationToken ct);
    Task DismissInputAsync(long userId, int messageId, CancellationToken ct);
}

using System.Globalization;
using EsimBot.DAL.Repos;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EsimBot.BLL.Services;

/// <summary>Keeps only the current bot UI in a private chat. Business records remain durable in the database.</summary>
public sealed class ChatUiService(ITelegramBotClient bot, IAppStateRepository state,
    ILogger<ChatUiService> logger) : IChatUiService
{
    private const int MaxTrackedMessages = 8;
    private static readonly AsyncLocal<int?> CurrentMessage = new();
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public IDisposable BeginScreen(int? messageId)
    {
        var previous = CurrentMessage.Value;
        CurrentMessage.Value = messageId;
        return new Scope(previous);
    }

    public async Task ShowAsync(long userId, string text, InlineKeyboardMarkup? keyboard,
        ParseMode parseMode, CancellationToken ct)
    {
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            var saved = await ReadAsync(userId, ct);
            var messageId = CurrentMessage.Value ?? saved.FirstOrDefault() switch { > 0 and var id => id, _ => (int?)null };
            foreach (var obsolete in saved.Where(id => id != messageId))
                await DeleteAsync(userId, obsolete, ct);

            if (messageId is not null)
            {
                try
                {
                    await bot.EditMessageText(userId, messageId.Value, text, parseMode: parseMode,
                        replyMarkup: keyboard, cancellationToken: ct);
                    await SaveAsync(userId, [messageId.Value], ct);
                    return;
                }
                catch (ApiRequestException ex) when (IsNotModified(ex))
                {
                    await SaveAsync(userId, [messageId.Value], ct);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (ApiRequestException ex) when (IsUnavailable(ex))
                {
                    logger.LogDebug("Chat screen was replaced ({ErrorType})", ex.GetType().Name);
                    await DeleteAsync(userId, messageId.Value, ct);
                }
            }

            var sent = await bot.SendMessage(userId, text, parseMode: parseMode, replyMarkup: keyboard,
                protectContent: true, cancellationToken: ct);
            await SaveAsync(userId, [sent.Id], ct);
        }
        finally { gate.Release(); }
    }

    public async Task ClearAsync(long userId, CancellationToken ct)
    {
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            var messageIds = (await ReadAsync(userId, ct)).ToList();
            if (CurrentMessage.Value is > 0 and var current && !messageIds.Contains(current))
                messageIds.Add(current);
            foreach (var messageId in messageIds.Distinct())
                await DeleteAsync(userId, messageId, ct);
            await SaveAsync(userId, [], ct);
        }
        finally { gate.Release(); }
    }

    public async Task TrackAsync(long userId, int messageId, bool replace, CancellationToken ct)
    {
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            var existing = await ReadAsync(userId, ct);
            if (replace)
                foreach (var obsolete in existing.Where(id => id != messageId))
                    await DeleteAsync(userId, obsolete, ct);
            var tracked = replace ? [] : existing.ToList();
            if (!tracked.Contains(messageId)) tracked.Add(messageId);
            await SaveAsync(userId, tracked.TakeLast(MaxTrackedMessages), ct);
        }
        finally { gate.Release(); }
    }

    public async Task DismissInputAsync(long userId, int messageId, CancellationToken ct)
    {
        try { await bot.DeleteMessage(userId, messageId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
        { logger.LogDebug("Customer input could not be removed ({ErrorType})", ex.GetType().Name); }
    }

    private async Task<IReadOnlyList<int>> ReadAsync(long userId, CancellationToken ct)
    {
        try
        {
            var saved = await state.GetAsync(Key(userId), ct);
            if (string.IsNullOrWhiteSpace(saved)) return [];
            return saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0).Distinct().TakeLast(MaxTrackedMessages).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Chat screen lookup failed ({ErrorType})", ex.GetType().Name);
            return [];
        }
    }

    private async Task SaveAsync(long userId, IEnumerable<int> messageIds, CancellationToken ct)
    {
        try
        {
            var value = string.Join(',', messageIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            await state.SetAsync(Key(userId), value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("Chat screen position was not saved ({ErrorType})", ex.GetType().Name); }
    }

    private async Task DeleteAsync(long userId, int messageId, CancellationToken ct)
    {
        try { await bot.DeleteMessage(userId, messageId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
        { logger.LogDebug("Previous chat screen could not be removed ({ErrorType})", ex.GetType().Name); }
    }

    private static string Key(long userId) => "ui:screen:" + userId.ToString(CultureInfo.InvariantCulture);
    private static bool IsNotModified(ApiRequestException ex) => ex.ErrorCode == 400
        && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
    private static bool IsUnavailable(ApiRequestException ex) => ex.ErrorCode == 400
        && (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("message can't be edited", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("message_id_invalid", StringComparison.OrdinalIgnoreCase));
    private static SemaphoreSlim Gate(long userId) => Gates[(int)(Math.Abs(userId) % Gates.Length)];

    private sealed class Scope(int? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentMessage.Value = previous;
        }
    }
}

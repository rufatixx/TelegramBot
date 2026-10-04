using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EsimBot.BLL.DTO;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EsimBot.BLL.Services;

/// <summary>Opt-in text relay with server-side reply routing; ticket text is never persisted.</summary>
public sealed class SupportService(ITelegramBotClient bot, IAppStateRepository state, ILanguageService languages,
    IOptions<BotOptions> options, ILogger<SupportService> logger, IChatUiService? chatUi = null) : ISupportService
{
    private const int TextLimit = 3000;
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    private long _nextCleanup;
    private long AdminId => options.Value.AdminUserId;

    public async Task BeginAsync(long userId, bool payment, CancellationToken ct)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        await CleanupAsync(ct);
        var t = await languages.GetAsync(userId, null, ct);
        if (AdminId <= 0)
        {
            await ShowAsync(userId, t.Text("support.unavailable"), ct);
            return;
        }
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            // The prompt discloses forwarding before any subsequent user text can be relayed.
            if (chatUi is not null) await chatUi.ClearAsync(userId, ct);
            var prompt = await SendAsync(userId, t.Text(userId == AdminId ? "support.admin_preview"
                : payment ? "support.payment_prompt" : "support.prompt"), ct, t.Text("support.input_placeholder"));
            if (chatUi is not null) await chatUi.TrackAsync(userId, prompt.Id, replace: true, ct);
            await state.SetAsync(ModeKey(userId), Encode(new Entry(Now() + 30 * 60,
                Topic: payment ? "payment" : "general", Preview: userId == AdminId, PromptMessageId: prompt.Id)), ct);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> HandleAsync(Message message, CancellationToken ct)
    {
        var user = message.From;
        if (message.Chat.Type != ChatType.Private || user is null || user.IsBot || user.Id <= 0 || message.Chat.Id != user.Id)
            return false;
        await CleanupAsync(ct);
        var gate = Gate(user.Id);
        await gate.WaitAsync(ct);
        try
        {
            var t = await languages.GetAsync(user.Id, user.LanguageCode, ct);
            var text = message.Text?.Trim();
            var command = text?.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
            if (command == "/cancel")
            {
                await state.DeleteAsync(ModeKey(user.Id), ct);
                if (chatUi is not null) await chatUi.DismissInputAsync(user.Id, message.Id, ct);
                await ShowAsync(user.Id, t.Text("support.cancelled"), ct);
                return true;
            }

            Entry? mode = null;
            if (AdminId > 0 && user.Id == AdminId)
            {
                if (command == "/reply")
                {
                    // User IDs in text are never interpreted as a recipient.
                    await state.DeleteAsync(ModeKey(user.Id), ct);
                    if (chatUi is not null) await chatUi.DismissInputAsync(user.Id, message.Id, ct);
                    await ShowAsync(user.Id, t.Text("support.admin_help"), ct);
                    return true;
                }
                // A slash command is an administrative action, even when sent as a reply.
                if (command?.StartsWith('/') == true)
                {
                    await state.DeleteAsync(ModeKey(user.Id), ct);
                    return false;
                }
                mode = await ReadAsync(ModeKey(user.Id), ct);
                if (message.ReplyToMessage is { } replied
                    && (mode?.Preview != true || replied.Id != mode.PromptMessageId))
                {
                    await ReplyAsync(message, text, t, ct);
                    return true;
                }
                // The administrator can explicitly exercise the customer flow. A normal reply still
                // uses its existing server-side route and never a recipient typed in the message.
                if (mode?.Preview != true) return false;
            }

            if (command?.StartsWith('/') == true)
            {
                await state.DeleteAsync(ModeKey(user.Id), ct);
                return false;
            }
            mode ??= await ReadAsync(ModeKey(user.Id), ct);
            if (mode is null || mode.Topic is not ("general" or "payment")) return false;
            if (text is null || text.Length is < 1 or > TextLimit)
            {
                if (chatUi is not null) await chatUi.DismissInputAsync(user.Id, message.Id, ct);
                await ShowAsync(user.Id, t.Text("support.text_only", TextLimit), ct);
                return true;
            }
            if (AdminId <= 0)
            {
                await state.DeleteAsync(ModeKey(user.Id), ct);
                if (chatUi is not null) await chatUi.DismissInputAsync(user.Id, message.Id, ct);
                await ShowAsync(user.Id, t.Text("support.unavailable"), ct);
                return true;
            }

            var adminText = await languages.GetAsync(AdminId, null, ct);
            Message delivered;
            try
            {
                delivered = await SendAsync(AdminId, adminText.Text(user.Id == AdminId ? "support.preview_incoming" : "support.incoming", user.Id,
                    adminText.Text(mode.Topic == "payment" ? "support.topic_payment" : "support.topic_general"), text), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("Support message delivery failed ({ExceptionType})", ex.GetType().Name);
                if (chatUi is not null) await chatUi.DismissInputAsync(user.Id, message.Id, ct);
                await ShowAsync(user.Id, t.Text("support.send_failed"), ct);
                return true;
            }
            await state.SetAsync(RouteKey(AdminId, delivered.Id), Encode(new Entry(Now() + 7 * 24 * 60 * 60,
                user.Id, Preview: user.Id == AdminId && mode.Preview)), ct);
            await state.DeleteAsync(ModeKey(user.Id), ct);
            if (chatUi is not null) await chatUi.DismissInputAsync(user.Id, message.Id, ct);
            await ShowAsync(user.Id, t.Text(user.Id == AdminId ? "support.preview_sent" : "support.sent"), ct);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task CancelAsync(long userId, CancellationToken ct)
    {
        if (userId <= 0) return;
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try { await state.DeleteAsync(ModeKey(userId), ct); }
        finally { gate.Release(); }
    }

    private async Task ReplyAsync(Message message, string? text, BotText adminText, CancellationToken ct)
    {
        var replied = message.ReplyToMessage!;
        if (replied.Chat.Id != AdminId || replied.From?.IsBot != true)
        {
            if (chatUi is not null) await chatUi.DismissInputAsync(AdminId, message.Id, ct);
            await ShowAsync(AdminId, adminText.Text("support.reply_invalid"), ct);
            return;
        }
        var key = RouteKey(AdminId, replied.Id);
        var route = await ReadAsync(key, ct);
        if (route is null || route.UserId <= 0 || route.UserId == AdminId && !route.Preview)
        {
            if (chatUi is not null) await chatUi.DismissInputAsync(AdminId, message.Id, ct);
            await ShowAsync(AdminId, adminText.Text("support.reply_invalid"), ct);
            return;
        }
        if (text is null || text.Length is < 1 or > TextLimit)
        {
            if (chatUi is not null) await chatUi.DismissInputAsync(AdminId, message.Id, ct);
            await ShowAsync(AdminId, adminText.Text("support.text_only", TextLimit), ct);
            return;
        }
        var recipientText = await languages.GetAsync(route.UserId, null, ct);
        try
        {
            await ShowAsync(route.UserId, recipientText.Text("support.reply_received", text), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Support reply delivery failed ({ExceptionType})", ex.GetType().Name);
            if (chatUi is not null) await chatUi.DismissInputAsync(AdminId, message.Id, ct);
            await ShowAsync(AdminId, adminText.Text("support.reply_failed"), ct);
            return;
        }
        await state.DeleteAsync(key, ct);
        if (chatUi is not null)
        {
            await chatUi.DismissInputAsync(AdminId, message.Id, ct);
            await chatUi.DismissInputAsync(AdminId, replied.Id, ct);
        }
        await ShowAsync(AdminId, adminText.Text("support.reply_sent"), ct);
    }

    private async Task<Entry?> ReadAsync(string key, CancellationToken ct)
    {
        var value = await state.GetAsync(key, ct);
        if (value is null) return null;
        Entry? result;
        try { result = JsonSerializer.Deserialize<Entry>(value, Json); }
        catch (JsonException) { result = null; }
        if (result is not null && result.ExpiresAt > Now()) return result;
        await state.DeleteAsync(key, ct);
        return null;
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = Now();
        var next = Interlocked.Read(ref _nextCleanup);
        if (now < next || Interlocked.CompareExchange(ref _nextCleanup, now + 300, next) != next) return;
        try { await state.DeleteExpiredSupportStateAsync(now, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        { logger.LogWarning("Expired support state cleanup deferred ({ExceptionType})", ex.GetType().Name); }
    }

    private Task<Message> SendAsync(long userId, string text, CancellationToken ct, string? replyPlaceholder = null) => bot.SendMessage(userId, text,
        protectContent: true, linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
        replyMarkup: replyPlaceholder is null ? null : new ForceReplyMarkup { InputFieldPlaceholder = replyPlaceholder }, cancellationToken: ct);
    private async Task ShowAsync(long userId, string text, CancellationToken ct)
    {
        if (chatUi is null) await SendAsync(userId, text, ct);
        else await chatUi.ShowAsync(userId, text, null, ParseMode.None, ct);
    }
    private static SemaphoreSlim Gate(long userId) => Gates[(int)(userId % Gates.Length)];
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string Encode(Entry entry) => JsonSerializer.Serialize(entry, Json);
    private static string ModeKey(long userId) => "support:mode:" + userId.ToString(CultureInfo.InvariantCulture);
    private static string RouteKey(long adminId, int messageId) => "support:route:" + adminId.ToString(CultureInfo.InvariantCulture)
        + ":" + messageId.ToString(CultureInfo.InvariantCulture);
    private sealed record Entry(long ExpiresAt, long UserId = 0, string? Topic = null, bool Preview = false, int PromptMessageId = 0);
}

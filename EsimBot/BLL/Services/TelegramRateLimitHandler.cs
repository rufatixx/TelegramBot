using System.Globalization;
using System.Text.Json;

namespace EsimBot.BLL.Services;

internal sealed class TelegramRateLimitHandler(ITelegramRequestGate gate) : DelegatingHandler
{
    private const int MaximumJsonBytes = 65536;
    private static readonly HashSet<string> MessageMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "sendMessage", "sendPhoto", "sendInvoice", "copyMessage", "copyMessages", "forwardMessage", "forwardMessages",
        "sendAudio", "sendDocument", "sendVideo", "sendAnimation", "sendVoice", "sendVideoNote", "sendMediaGroup",
        "sendLocation", "sendVenue", "sendContact", "sendPoll", "sendDice", "sendSticker", "sendGame", "sendPaidMedia",
        "editMessageText", "editMessageCaption", "editMessageReplyMarkup"
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/');
        if (method is null || !MessageMethods.Contains(method))
            return await base.SendAsync(request, ct);

        var chatId = await ChatIdAsync(request.Content, ct);
        using var admission = await gate.AcquireAsync(chatId, ct);
        // No automatic write retries: a timeout or 429 must reach the durable/user-facing caller.
        return await base.SendAsync(request, ct);
    }

    private static async Task<long> ChatIdAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is MultipartContent multipart)
        {
            foreach (var part in multipart)
            {
                if (!string.Equals(part.Headers.ContentDisposition?.Name?.Trim('"'), "chat_id", StringComparison.Ordinal)) continue;
                // Only the scalar part is inspected; never enumerate/read any attached file or the whole multipart body.
                if (part is not StringContent || part.Headers.ContentLength is > 64) throw InvalidMessage();
                var value = await part.ReadAsStringAsync(ct);
                if (long.TryParse(value.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId)) return chatId;
                throw InvalidMessage();
            }
            throw InvalidMessage();
        }
        if (content is null || content.Headers.ContentType?.MediaType != "application/json"
            || content.Headers.ContentLength is > MaximumJsonBytes) throw InvalidMessage();
        try
        {
            // Preserve the exact bytes for the actual request while bounding JSON buffering.
            await content.LoadIntoBufferAsync(MaximumJsonBytes);
            using var document = JsonDocument.Parse(await content.ReadAsStringAsync(ct));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("chat_id", out var value)) throw InvalidMessage();
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
            throw InvalidMessage();
        }
        catch (JsonException) { throw InvalidMessage(); }
        catch (HttpRequestException) { throw InvalidMessage(); }
    }

    private static InvalidOperationException InvalidMessage() => new("Telegram message has an unsupported chat identifier or payload.");
}

using System.Text.Json;
using EsimBot.BLL.DTO;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace EsimBot.BLL.Services;

public sealed class TelegramWebhookService(IOptions<BotOptions> options, IBotRuntimeState runtime,
    ILogger<TelegramWebhookService> logger, IBotHandler? handler = null, IUpdateDispatcher? dispatcher = null) : ITelegramWebhookService
{
    public async Task<ApiResponse> ReceiveAsync(HttpContext context, CancellationToken ct)
    {
        var config = options.Value;
        if (config.Mode != "Webhook") return Reply(StatusCodes.Status404NotFound);
        var header = context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"];
        if (header.Count != 1 || !WebhookSecurity.VerifyWebhookSecret(config.WebhookSecret, header[0])) return Reply(StatusCodes.Status401Unauthorized);
        if (!runtime.IsReady || (dispatcher is null && handler is null)) return Reply(StatusCodes.Status503ServiceUnavailable);
        if (!context.Request.HasJsonContentType()) return Reply(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > WebhookSecurity.MaxWebhookBytes) return Reply(StatusCodes.Status413PayloadTooLarge);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = WebhookSecurity.MaxWebhookBytes;
        try
        {
            // Bound chunked requests and in-memory hosts where Kestrel's limit does not apply.
            await using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer, ct);
                if (read == 0) break;
                if (body.Length + read > WebhookSecurity.MaxWebhookBytes) return Reply(StatusCodes.Status413PayloadTooLarge);
                await body.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var update = JsonSerializer.Deserialize<Update>(body.GetBuffer().AsSpan(0, (int)body.Length), JsonBotAPI.Options);
            if (update is null) return Reply(StatusCodes.Status400BadRequest);
            if (dispatcher is not null)
            {
                var financial = update.PreCheckoutQuery is not null || update.Message?.SuccessfulPayment is not null
                    || update.Message?.RefundedPayment is not null;
                var accepted = financial ? await dispatcher.HandlePriorityAsync(update, ct)
                    : dispatcher.TryQueueInteractive(update);
                if (!accepted) return Reply(StatusCodes.Status503ServiceUnavailable);
            }
            else
            {
                // Compatibility for isolated service fixtures; production always supplies the shared dispatcher.
                await handler!.HandleAsync(update, ct);
            }
            return Reply(StatusCodes.Status200OK);
        }
        catch (JsonException) { return Reply(StatusCodes.Status400BadRequest); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return Reply(StatusCodes.Status413PayloadTooLarge); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return Reply(StatusCodes.Status503ServiceUnavailable); }
        catch (Exception ex)
        {
            logger.LogError("Telegram webhook processing failed ({ErrorType})", ex.GetType().Name);
            return Reply(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static ApiResponse Reply(int statusCode) => new(statusCode, new { status = statusCode });
}

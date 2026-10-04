using System.Globalization;
using EsimBot.BLL.DTO;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;

namespace EsimBot.BLL.Services;

/// <summary>Telegram test invoices and refunds only. Deliberately has no eSIM provider dependency.</summary>
public sealed class PaymentTestService(
    ITelegramBotClient bot, IOrderService orders, ILanguageService languages,
    IOptions<PaymentTestOptions> options, IOptions<BotOptions> botOptions,
    ILogger<PaymentTestService> logger, IChatUiService? chatUi = null) : IPaymentTestService
{
    private PaymentTestOptions Settings => options.Value;

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (!Settings.Enabled || !botOptions.Value.TestEnvironment)
            throw new InvalidOperationException("Payment test service requires Telegram test environment.");
        if (update.PreCheckoutQuery is { } checkout)
        {
            await CheckoutAsync(checkout, ct);
            return;
        }
        if (update.Message?.SuccessfulPayment is { } payment)
        {
            if (payment.Currency != "XTR") throw new InvalidOperationException("Unexpected test payment currency.");
            var payer = update.Message.From?.Id ?? throw new InvalidOperationException("Test payment has no payer.");
            var order = await orders.GetAsync(payment.InvoicePayload, null, ct);
            var acceptedScope = update.Message.Chat.Type == ChatType.Private && update.Message.Chat.Id == payer
                && IsAdmin(payer) && order?.UserId == payer && order.PackageCode == PaymentTestOptions.PackageCode;
            // Preserve even unexpected charges. OrderService queues foreign/duplicate charges for refund.
            var result = await orders.RecordPaymentAsync(payer, payment.InvoicePayload,
                payment.TelegramPaymentChargeId, payment.TotalAmount, ct);
            var refundQueued = result.RefundQueued;
            if (result.Accepted && !acceptedScope)
                refundQueued = await orders.QueueUnsubmittedRefundAsync(payment.InvoicePayload, ct);
            if (chatUi is not null) await chatUi.DismissInputAsync(payer, update.Message.Id, ct);
            var key = refundQueued ? "testing.refund_queued"
                : result.Accepted && acceptedScope ? result.Duplicate ? "testing.duplicate" : "testing.paid"
                : "testing.review";
            await FinancialReceiptAsync(payer, update.Message.From?.LanguageCode, key,
                result.Accepted && acceptedScope && !refundQueued ? payment.InvoicePayload : null, ct);
            return;
        }
        if (update.Message?.RefundedPayment is { } refund)
        {
            if (refund.Currency != "XTR" || update.Message.Chat.Type != ChatType.Private)
                throw new InvalidOperationException("Unexpected test refund scope.");
            var owner = update.Message.Chat.Id;
            var reconciliation = await orders.RecordExternalRefundAsync(owner, refund.TelegramPaymentChargeId, ct);
            if (reconciliation is not null)
                logger.LogWarning("Test refund requires reconciliation for order {OrderId}", reconciliation);
            if (chatUi is not null) await chatUi.DismissInputAsync(owner, update.Message.Id, ct);
            await FinancialReceiptAsync(owner, null, "testing.refunded", null, ct);
            return;
        }

        var callback = update.CallbackQuery;
        var message = callback?.Message ?? update.Message;
        var user = callback?.From ?? message?.From;
        if (message?.Chat.Type != ChatType.Private || user is null || user.IsBot || message.Chat.Id != user.Id) return;
        var t = Fallback(user.LanguageCode);
        using var screen = chatUi?.BeginScreen(callback?.Message?.Id);
        try
        {
            t = await languages.GetAsync(user.Id, user.LanguageCode, ct);
            if (callback is not null) await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            else if (chatUi is not null) await chatUi.DismissInputAsync(user.Id, message.Id, ct);
            var text = message.Text?.Trim() ?? "";
            var command = text.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
            // A test account has a different ID. This must work before an administrator is configured.
            if (callback is null && command == "/id")
            {
                await SendAsync(user.Id, t.Text("testing.id", user.Id), ct);
                return;
            }
            if (!IsAdmin(user.Id))
            {
                await SendAsync(user.Id, t.Text("testing.not_allowed"), ct);
                return;
            }

            if (callback is not null)
            {
                var parts = (callback.Data ?? "").Split(':', 2);
                switch (parts[0])
                {
                    case "testpay": await InvoiceAsync(t, user.Id, "test:callback:" + callback.Id, ct); return;
                    case "testorders": await OrdersAsync(t, user.Id, parts.Length == 2 ? Page(parts[1]) : 0, ct); return;
                    case "testrefund" when parts.Length == 2: await RefundAsync(t, user.Id, parts[1], ct); return;
                    case "testlanguage": await LanguageMenuAsync(t, user.Id, ct); return;
                    case "testlang" when parts.Length == 2 && BotText.SupportedCodes.Contains(parts[1]):
                        await languages.SetAsync(user.Id, parts[1], ct);
                        var selected = await languages.GetAsync(user.Id, parts[1], ct);
                        await SendAsync(user.Id, selected.Text("testing.language_saved"), ct, Menu(selected)); return;
                    default: await WelcomeAsync(t, user.Id, ct); return;
                }
            }
            switch (command)
            {
                case "/testpayment": await InvoiceAsync(t, user.Id, "test:update:" + update.Id.ToString(CultureInfo.InvariantCulture), ct); break;
                case "/orders": await OrdersAsync(t, user.Id, 0, ct); break;
                case "/language": await LanguageMenuAsync(t, user.Id, ct); break;
                case "/refund":
                    var id = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)?.Trim();
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 64) await SendAsync(user.Id, t.Text("testing.refund_usage"), ct);
                    else await RefundAsync(t, user.Id, id, ct);
                    break;
                default: await WelcomeAsync(t, user.Id, ct); break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Test payment UI unavailable ({ExceptionType})", ex.GetType().Name);
            await SendAsync(user.Id, t.Text("testing.failed"), ct);
        }
    }

    private async Task CheckoutAsync(PreCheckoutQuery query, CancellationToken ct)
    {
        var t = Fallback(query.From.LanguageCode);
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            t = await languages.GetAsync(query.From.Id, query.From.LanguageCode, timeout.Token);
            if (!IsAdmin(query.From.Id)) error = "testing.checkout_denied";
            else
            {
                var order = await orders.GetAsync(query.InvoicePayload, query.From.Id, timeout.Token);
                if (order is null || order.UserId != query.From.Id || order.PackageCode != PaymentTestOptions.PackageCode
                    || order.State != "quoted" || order.ExpiresAt <= DateTime.UtcNow)
                    error = "testing.checkout_invalid";
                else if (query.Currency != "XTR" || order.Stars != query.TotalAmount || order.Stars is < 1 or > 100)
                    error = "testing.checkout_amount";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Test checkout validation unavailable ({ExceptionType})", ex.GetType().Name);
            error = "testing.checkout_unavailable";
        }
        try { await bot.AnswerPreCheckoutQuery(query.Id, error is null ? null : t.Text(error), ct); }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
            (ex.Message.Contains("QUERY_ID_INVALID", StringComparison.OrdinalIgnoreCase)
             || ex.Message.Contains("QUERY_ALREADY_ANSWERED", StringComparison.OrdinalIgnoreCase)
             || ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase)
             || ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase)
             || ex.Message.Contains("query has already been answered", StringComparison.OrdinalIgnoreCase)))
        { logger.LogWarning("Test checkout query is no longer answerable ({ExceptionType})", ex.GetType().Name); }
    }

    private async Task InvoiceAsync(BotText t, long userId, string requestKey, CancellationToken ct)
    {
        var package = new EsimPackage(PaymentTestOptions.PackageCode, "TEST PAYMENT — NO eSIM", 1, "USD", 0,
            0, "DAY", [], "", "", "", "test");
        var order = await orders.CreateQuoteAsync(userId, package, Settings.InvoiceStars, "payment-test-v1", requestKey, ct);
        if (order.UserId != userId || order.PackageCode != PaymentTestOptions.PackageCode || order.State != "quoted"
            || order.ExpiresAt <= DateTime.UtcNow || order.Stars is < 1 or > 100)
        {
            await SendAsync(userId, t.Text("testing.checkout_invalid"), ct, Menu(t));
            return;
        }
        if (chatUi is not null) await chatUi.ClearAsync(userId, ct);
        var invoice = await bot.SendInvoice(userId, t.Text("testing.invoice_title"), t.Text("testing.invoice_description", order.Id),
            order.Id, "XTR", [new LabeledPrice(t.Text("testing.invoice_label"), order.Stars)], providerToken: "",
            startParameter: "test_" + order.Id, protectContent: true, cancellationToken: ct);
        if (chatUi is not null) await chatUi.TrackAsync(userId, invoice.Id, replace: true, ct);
    }

    private async Task RefundAsync(BotText t, long userId, string id, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, userId, ct);
        var queued = order is { State: "paid" } && order.UserId == userId && order.PackageCode == PaymentTestOptions.PackageCode
            && await orders.QueueUnsubmittedRefundAsync(id, ct);
        await SendAsync(userId, t.Text(queued ? "testing.refund_queued" : "testing.refund_unavailable"), ct, Menu(t));
    }

    private async Task OrdersAsync(BotText t, long userId, int page, CancellationToken ct)
    {
        var found = await orders.ListAsync(userId, page, ct);
        var visible = found.Where(order => order.UserId == userId && order.PackageCode == PaymentTestOptions.PackageCode).ToArray();
        var text = visible.Length == 0 ? t.Text("testing.no_orders") : t.Text("testing.orders_header", page + 1)
            + string.Join("\n", visible.Select(order => t.Text("testing.order_line", order.Id, State(t, order), order.Stars)));
        var rows = visible.Where(order => order.State == "paid")
            .Select(order => new[] { Button(t.Text("testing.refund_button_order", order.Id[..Math.Min(8, order.Id.Length)], order.Stars), "testrefund:" + order.Id) }).ToList();
        var pages = new List<InlineKeyboardButton>();
        if (page > 0) pages.Add(Button("←", $"testorders:{page - 1}"));
        if (found.Count == 8) pages.Add(Button("→", $"testorders:{page + 1}"));
        if (pages.Count != 0) rows.Add(pages.ToArray());
        rows.Add([Button(t.Text("testing.button_menu"), "testmenu")]);
        await SendAsync(userId, text, ct, new(rows));
    }

    private async Task FinancialReceiptAsync(long userId, string? telegramLanguageCode, string key, string? refundableOrderId, CancellationToken ct)
    {
        try
        {
            var t = await languages.GetAsync(userId, telegramLanguageCode, ct);
            var keyboard = refundableOrderId is null ? Menu(t)
                : new InlineKeyboardMarkup(new[] { new[] { Button(t.Text("testing.button_refund"), "testrefund:" + refundableOrderId) } });
            await SendAsync(userId, t.Text(key), ct, keyboard);
        }
        catch (Exception ex)
        { logger.LogWarning("Saved test payment receipt unavailable ({ExceptionType})", ex.GetType().Name); }
    }

    private Task WelcomeAsync(BotText t, long userId, CancellationToken ct) => SendAsync(userId,
        t.Text("testing.welcome", Settings.InvoiceStars), ct, Menu(t));
    private Task LanguageMenuAsync(BotText t, long userId, CancellationToken ct) => SendAsync(userId,
        t.Text("testing.language_prompt"), ct, new InlineKeyboardMarkup(BotText.SupportedCodes
            .Select(code => new[] { Button(t.Text("ui.language_" + code), "testlang:" + code) })));
    private static InlineKeyboardMarkup Menu(BotText t) => new(new[]
    {
        new[] { Button(t.Text("testing.button_pay"), "testpay") },
        new[] { Button(t.Text("testing.button_orders"), "testorders:0"), Button(t.Text("ui.button_language"), "testlanguage") }
    });
    private bool IsAdmin(long id) => Settings.IsConfigured && id > 0 && id == Settings.AdminUserId;
    private BotText Fallback(string? languageCode)
    {
        var code = languageCode?.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return new BotText(code is not null && BotText.SupportedCodes.Contains(code) ? code : botOptions.Value.DefaultLanguage);
    }
    private static string State(BotText t, Order order) => t.Text(order.State switch
    {
        "quoted" => order.ExpiresAt <= DateTime.UtcNow ? "ui.state_expired" : "ui.state_quoted",
        "paid" => "ui.state_paid", "refund_pending" => "ui.state_refund_pending", "refunded" => "ui.state_refunded",
        _ => "ui.state_manual_review"
    });
    private static int Page(string value) => int.TryParse(value, out var page) ? Math.Clamp(page, 0, 10000) : 0;
    private static InlineKeyboardButton Button(string text, string data) => InlineKeyboardButton.WithCallbackData(text, data);
    private Task SendAsync(long userId, string text, CancellationToken ct, InlineKeyboardMarkup? markup = null)
        => chatUi is null
            ? bot.SendMessage(userId, text, replyMarkup: markup, protectContent: true, cancellationToken: ct)
            : chatUi.ShowAsync(userId, text, markup, ParseMode.None, ct);
}

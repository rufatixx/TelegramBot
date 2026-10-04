using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EsimBot.BLL.DTO;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;

namespace EsimBot.BLL.Services;

public sealed class BotHandler(
    ITelegramBotClient bot, IShopService shop, IBotNotifier notifier, IPricing pricing,
    IOptions<SalesOptions> salesOptions, IOptions<BotOptions> botOptions, ILogger<BotHandler> logger, ILanguageService languages,
    IPaymentTestService? paymentTests = null, ISupportService? support = null,
    IChatUiService? chatUi = null, IAdminStatisticsService? adminStatistics = null) : IBotHandler
{
    private const int CountryPageSize = 12;
    private const int PackagePageSize = 6;
    private const int OrderPageSize = 8;
    private static readonly CompareInfo SearchComparer = CultureInfo.InvariantCulture.CompareInfo;
    private SalesOptions Sales => salesOptions.Value;

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (botOptions.Value.TestEnvironment)
        {
            if (paymentTests is null) throw new InvalidOperationException("Test payment handler is unavailable.");
            await paymentTests.HandleAsync(update, ct);
            return;
        }
        // Money updates are durably recorded before language lookup and best-effort receipts.
        if (update.PreCheckoutQuery is { } checkout)
        {
            await CheckoutAsync(checkout, ct);
            return;
        }
        if (update.Message?.RefundedPayment is { } refund)
        {
            if (refund.Currency != "XTR" || update.Message.Chat.Type != ChatType.Private)
                throw new InvalidOperationException("Unexpected refund requires reconciliation.");
            var owner = update.Message.Chat.Id;
            await shop.RecordRefundAsync(owner, refund.TelegramPaymentChargeId, ct);
            if (chatUi is not null) await chatUi.DismissInputAsync(owner, update.Message.Id, ct);
            await NotifyPersistedPaymentAsync(owner, null, "ui.refund_confirmed", ct);
            return;
        }
        if (update.Message?.SuccessfulPayment is { } payment)
        {
            var payer = update.Message.From?.Id ?? throw new InvalidOperationException("Payment has no payer.");
            var result = await shop.RecordPaymentAsync(payer, payment.InvoicePayload,
                payment.TelegramPaymentChargeId, payment.Currency, payment.TotalAmount, ct);
            if (chatUi is not null) await chatUi.DismissInputAsync(payer, update.Message.Id, ct);
            var key = result.RefundQueued ? "ui.payment_refund_queued"
                : result.Accepted ? result.Duplicate ? "ui.payment_duplicate" : "ui.payment_accepted"
                : "ui.payment_review";
            await NotifyPersistedPaymentAsync(payer, update.Message.From?.LanguageCode, key, ct);
            return;
        }

        var callback = update.CallbackQuery;
        var message = callback?.Message ?? update.Message;
        var user = callback?.From ?? message?.From;
        if (message?.Chat.Type != ChatType.Private || user is null || user.IsBot || message.Chat.Id != user.Id)
            return;

        var t = FallbackText(user.LanguageCode);
        try
        {
            // End the button spinner before database lookups or supplier requests.
            if (callback is not null) await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            t = await languages.GetAsync(user.Id, user.LanguageCode, ct);
            using var screen = chatUi?.BeginScreen(callback?.Message?.Id);
            if (callback is not null)
            {
                if (support is not null) await support.CancelAsync(user.Id, ct);
                await CallbackAsync(t, user.Id, callback, ct);
            }
            else if (support is not null && await support.HandleAsync(message, ct)) return;
            else if (message.Text is { } text)
                await TextAsync(t, user.Id, message.Id, text.Trim(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ShopException)
        {
            await ScreenAsync(user.Id, t.Text("ui.action_unavailable"), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Bot action failed ({ExceptionType})", ex.GetType().Name);
            await ScreenAsync(user.Id, t.Text("ui.action_failed"), ct);
        }
    }

    public async Task NotifyBusyAsync(Update update, CancellationToken ct)
    {
        var callback = update.CallbackQuery;
        var message = callback?.Message ?? update.Message;
        var user = callback?.From ?? message?.From;
        if (message?.Chat.Type != ChatType.Private || user is null || user.IsBot || message.Chat.Id != user.Id)
            return;
        var text = FallbackText(user.LanguageCode).Text("ui.busy");
        if (callback is not null)
            await bot.AnswerCallbackQuery(callback.Id, text, showAlert: true, cancellationToken: ct);
        else
            await ScreenAsync(user.Id, text, ct);
    }

    private async Task CheckoutAsync(PreCheckoutQuery query, CancellationToken ct)
    {
        string? error;
        var t = FallbackText(query.From.LanguageCode);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            t = await languages.GetAsync(query.From.Id, query.From.LanguageCode, timeout.Token);
            error = await shop.ValidateCheckoutAsync(query.From.Id, query.InvoicePayload,
                query.Currency, query.TotalAmount, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Checkout validation unavailable ({ExceptionType})", ex.GetType().Name);
            error = "ui.checkout_unavailable";
        }
        try
        {
            await bot.AnswerPreCheckoutQuery(query.Id, error is null ? null : Limit(t.Text(error), 190), ct);
        }
        catch (ApiRequestException ex) when (IsExpiredCheckout(ex))
        {
            // Expired or already answered queries must not block later financial updates.
            logger.LogWarning("Checkout query is no longer answerable ({ExceptionType})", ex.GetType().Name);
        }
    }

    private async Task NotifyPersistedPaymentAsync(long userId, string? telegramLanguageCode, string key, CancellationToken ct)
    {
        try
        {
            using var receipt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            receipt.CancelAfter(TimeSpan.FromSeconds(2));
            var t = await languages.GetAsync(userId, telegramLanguageCode, receipt.Token);
            await ScreenAsync(userId, t.Text(key), receipt.Token);
        }
        catch (Exception ex)
        {
            // Accounting has succeeded. Even a failed preference lookup must not replay it forever.
            logger.LogWarning("Saved payment receipt could not be sent ({ExceptionType})", ex.GetType().Name);
        }
    }

    private static bool IsExpiredCheckout(ApiRequestException error) => error.ErrorCode == 400
        && (error.Message.Contains("QUERY_ID_INVALID", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("QUERY_ALREADY_ANSWERED", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("query has already been answered", StringComparison.OrdinalIgnoreCase));

    private async Task TextAsync(BotText t, long userId, int messageId, string text, CancellationToken ct)
    {
        var command = text.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        if (chatUi is not null)
            await chatUi.DismissInputAsync(userId, messageId, ct);
        switch (command)
        {
            case "/start":
                var startTarget = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)?.Trim();
                if (startTarget == "support") { await SupportAsync(t, userId, false, ct); return; }
                if (startTarget == "terms") { await TermsAsync(t, userId, ct); return; }
                await MenuAsync(t, userId, ct); return;
            case "/menu":
                await MenuAsync(t, userId, ct); return;
            case "/catalog":
                await CountriesAsync(t, userId, 0, ct); return;
            case "/orders":
                await OrdersAsync(t, userId, 0, ct); return;
            case "/install":
                await InstallAsync(t, userId, ct); return;
            case "/support": case "/paysupport":
                await SupportAsync(t, userId, command == "/paysupport", ct); return;
            case "/terms":
                await TermsAsync(t, userId, ct); return;
            case "/id":
                await ScreenAsync(userId, t.Text("ui.id", userId), ct, MainKeyboard(t, userId)); return;
            case "/help":
                await ScreenAsync(userId, t.Text("ui.help"), ct, MainKeyboard(t, userId)); return;
            case "/language":
                await LanguageAsync(t, userId, ct); return;
            case "/stats":
                await AdminStatisticsAsync(t, userId, ct); return;
            case "/refund": case "/retry":
                if (botOptions.Value.AdminUserId != userId || userId <= 0) return;
                var id = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)?.Trim();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
                {
                    await ScreenAsync(userId, t.Text("ui.admin_usage", command), ct, MainKeyboard(t, userId)); return;
                }
                if (command == "/retry")
                {
                    var queued = await shop.RequestRetryAsync(userId, id, ct);
                    await ScreenAsync(userId, queued
                        ? t.Text("ui.retry_queued")
                        : t.Text("ui.retry_unavailable"), ct, MainKeyboard(t, userId)); return;
                }
                var accepted = await shop.RequestRefundAsync(userId, id, ct);
                await ScreenAsync(userId, accepted
                    ? t.Text("ui.refund_queued")
                    : t.Text("ui.refund_unavailable"), ct, MainKeyboard(t, userId)); return;
        }
        if (text.StartsWith('/')) { await MenuAsync(t, userId, ct); return; }
        if (text.Length is < 2 or > 80)
        {
            await ScreenAsync(userId, t.Text("ui.search_hint"), ct, MainKeyboard(t, userId)); return;
        }
        await CountriesAsync(t, userId, 0, ct, text);
    }

    private Task MenuAsync(BotText t, long userId, CancellationToken ct) => ScreenAsync(userId,
        t.Text("ui.welcome") + (!shop.Readiness.CanBrowse ? t.Text("ui.catalog_not_connected_note")
            : !shop.Readiness.CanBuy ? t.Text("ui.browse_only_note") : ""), ct, MainKeyboard(t, userId));

    private InlineKeyboardMarkup MainKeyboard(BotText t, long userId)
    {
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { Button(t.Text("ui.button_country"), "countries:0") },
            new[] { Button(t.Text("ui.button_orders"), "orders:0"), Button(t.Text("ui.button_install"), "install") },
            new[] { Button(t.Text("ui.button_support"), "support"), Button(t.Text("ui.button_terms"), "terms") },
            new[] { Button(t.Text("ui.button_language"), "language") }
        };
        if (IsAdmin(userId)) rows.Add(new[] { Button(t.Text("ui.button_admin_statistics"), "adminstats") });
        return new InlineKeyboardMarkup(rows);
    }

    private Task LanguageAsync(BotText t, long userId, CancellationToken ct) => ScreenAsync(userId,
        t.Text("ui.language_prompt"), ct, new InlineKeyboardMarkup(new[]
        {
            new[] { Button(t.Text("ui.language_az"), "lang:az") },
            new[] { Button(t.Text("ui.language_ru"), "lang:ru") },
            new[] { Button(t.Text("ui.language_en"), "lang:en") },
            new[] { Button(t.Text("ui.button_menu"), "menu") }
        }));

    private async Task SetChatCommandsAsync(BotText t, long userId, CancellationToken ct)
    {
        try
        {
            var commands = new List<BotCommand>
            {
                new BotCommand { Command = "start", Description = t.Text("ui.command_start") },
                new BotCommand { Command = "orders", Description = t.Text("ui.command_orders") },
                new BotCommand { Command = "install", Description = t.Text("ui.command_install") },
                new BotCommand { Command = "support", Description = t.Text("ui.command_support") },
                new BotCommand { Command = "paysupport", Description = t.Text("ui.command_paysupport") },
                new BotCommand { Command = "terms", Description = t.Text("ui.command_terms") },
                new BotCommand { Command = "language", Description = t.Text("ui.command_language") }
            };
            if (IsAdmin(userId))
                commands.Add(new BotCommand { Command = "stats", Description = t.Text("ui.command_admin_statistics") });
            await bot.SetMyCommands(commands, scope: new BotCommandScopeChat { ChatId = userId }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // The preference is saved; command-menu refresh may fail independently.
            logger.LogWarning("Language command menu update failed ({ExceptionType})", ex.GetType().Name);
        }
    }

    private async Task CallbackAsync(BotText t, long userId, CallbackQuery query, CancellationToken ct)
    {
        var data = query.Data ?? "";
        var parts = data.Split(':');
        switch (parts[0])
        {
            case "menu": await MenuAsync(t, userId, ct); break;
            case "language": await LanguageAsync(t, userId, ct); break;
            case "adminstats": await AdminStatisticsAsync(t, userId, ct); break;
            case "lang" when parts.Length == 2 && parts[1] is "az" or "ru" or "en":
                await languages.SetAsync(userId, parts[1], ct);
                var selected = await languages.GetAsync(userId, parts[1], ct);
                await SetChatCommandsAsync(selected, userId, ct);
                await ScreenAsync(userId, selected.Text("ui.language_saved"), ct, MainKeyboard(selected, userId));
                break;
            case "countries" when parts.Length == 2:
                await CountriesAsync(t, userId, Page(parts[1]), ct); break;
            case "country" when parts.Length == 3:
                await PackagesAsync(t, userId, parts[1], Page(parts[2]), ct); break;
            case "package" when parts.Length == 2:
                await PackageAsync(t, userId, parts[1], ct); break;
            case "agree" when parts.Length == 3:
                if (parts[2] != Key(Sales.TermsVersion))
                {
                    await ScreenAsync(userId, t.Text("ui.terms_changed"), ct, MainKeyboard(t, userId)); break;
                }
                var package = await FindPackageAsync(parts[1], ct);
                if (package is null) { await StaleAsync(t, userId, ct); break; }
                var order = await shop.QuoteAsync(userId, package.Code, query.Id, ct);
                await InvoiceAsync(t, userId, order, ct); break;
            case "orders" when parts.Length == 2:
                await OrdersAsync(t, userId, Page(parts[1]), ct); break;
            case "order" when parts.Length == 2:
                await OrderAsync(t, userId, parts[1], ct); break;
            case "pay" when parts.Length == 2:
                var unpaid = await shop.GetOrderAsync(userId, parts[1], ct);
                if (unpaid is null || unpaid.State != "quoted" || unpaid.ExpiresAt <= DateTime.UtcNow)
                    await StaleAsync(t, userId, ct);
                else await InvoiceAsync(t, userId, unpaid, ct);
                break;
            case "qr" when parts.Length == 2:
                var ownOrder = await shop.GetOrderAsync(userId, parts[1], ct);
                var profile = ownOrder is { State: "ready" } ? await shop.GetProfileAsync(userId, ownOrder.Id, ct) : null;
                if (ownOrder is null || profile is null) await StaleAsync(t, userId, ct);
                else await notifier.DeliverAsync(ownOrder, profile, ct);
                break;
            case "usage" when parts.Length == 2:
                await UsageAsync(t, userId, parts[1], ct); break;
            case "install": await InstallAsync(t, userId, ct); break;
            case "support": await SupportAsync(t, userId, false, ct); break;
            case "terms": await TermsAsync(t, userId, ct); break;
            default: await StaleAsync(t, userId, ct); break;
        }
    }

    private async Task CountriesAsync(BotText t, long userId, int page, CancellationToken ct, string? search = null)
    {
        if (!await EnsureCatalogAsync(t, userId, ct)) return;
        var packages = await shop.GetPackagesAsync(ct);
        var countries = packages.SelectMany(x => x.Countries).DistinctBy(x => x.Code)
            .Where(x => search is null || t.CountrySearchNames(x).Any(name => Matches(name, search)))
            .OrderBy(t.Country, StringComparer.Create(t.Culture, true)).ToArray();
        if (countries.Length == 0)
        {
            await ScreenAsync(userId, search is null
                ? t.Text("ui.no_plans")
                : t.Text("ui.country_not_found"), ct); return;
        }
        if (search is not null && countries.Length == 1)
        {
            await PackagesAsync(t, userId, Key(countries[0].Code), 0, ct); return;
        }
        page = Math.Min(page, (countries.Length - 1) / CountryPageSize);
        var rows = countries.Skip(page * CountryPageSize).Take(CountryPageSize)
            .Chunk(2).Select(chunk => chunk.Select(c => Button(Limit(DisplayCountry(t, c), 26), $"country:{Key(c.Code)}:0")).ToArray()).ToList();
        // Search results are intentionally limited to one view; a full-country pager must
        // never silently change a search into an unrelated next page.
        if (search is null) AddPager(rows, "countries", page, countries.Length, CountryPageSize);
        rows.Add([Button(t.Text("ui.button_menu"), "menu")]);
        await ScreenAsync(userId, search is null
            ? t.Text("ui.countries_page", countries.Length, page + 1, (countries.Length + CountryPageSize - 1) / CountryPageSize)
            : t.Text("ui.search_results", Limit(search, 80)) + (countries.Length > CountryPageSize ? t.Text("ui.search_refine") : t.Text("ui.choose_country")), ct, new(rows));
    }

    private async Task PackagesAsync(BotText t, long userId, string countryKey, int page, CancellationToken ct)
    {
        if (!await EnsureCatalogAsync(t, userId, ct)) return;
        var all = await shop.GetPackagesAsync(ct);
        var country = all.SelectMany(p => p.Countries).DistinctBy(c => c.Code).SingleOrDefault(c => Key(c.Code) == countryKey);
        if (country is null) { await StaleAsync(t, userId, ct); return; }
        var packages = all.Where(p => p.Countries.Any(c => c.Code == country.Code))
            .OrderBy(p => Sales.IsConfigured ? RetailStars(p) ?? long.MaxValue : p.PriceUnits)
            .ThenBy(p => p.VolumeBytes).ThenBy(p => p.Duration).ToArray();
        page = Math.Min(page, (packages.Length - 1) / PackagePageSize);
        var rows = packages.Skip(page * PackagePageSize).Take(PackagePageSize)
            .Select(p => new[] { Button(t.Text("ui.plan_button", Volume(t, p.VolumeBytes), Duration(t, p), Price(t, p)), $"package:{Key(p.Code)}") }).ToList();
        AddPager(rows, $"country:{countryKey}", page, packages.Length, PackagePageSize);
        rows.Add([Button(t.Text("ui.button_all_countries"), "countries:0"), Button(t.Text("ui.button_menu"), "menu")]);
        await ScreenAsync(userId, t.Text("ui.plans_page", DisplayCountry(t, country), page + 1, (packages.Length + PackagePageSize - 1) / PackagePageSize)
            + (!shop.Readiness.CanBuy ? t.Text("ui.sales_unavailable_note") : ""), ct, new(rows));
    }

    private async Task PackageAsync(BotText t, long userId, string key, CancellationToken ct)
    {
        var package = await FindPackageAsync(key, ct);
        if (package is null) { await StaleAsync(t, userId, ct); return; }
        var canBuy = shop.Readiness.CanBuy && RetailStars(package) is not null;
        var coverage = string.Join(", ", package.Countries.Take(16).Select(country => DisplayCountry(t, country)));
        if (package.Countries.Count > 16) coverage += t.Text("ui.coverage_more", package.Countries.Count - 16);
        var text = t.Text("ui.plan_details", Html(Limit(package.Name, 140)), Html(Volume(t, package.VolumeBytes)),
            Html(Duration(t, package)), Html(Price(t, package)), Html(coverage),
            Html(string.IsNullOrWhiteSpace(package.Speed) ? t.Text("ui.network_unspecified") : Limit(package.Speed, 100)),
            Html(t.Activation(package.ActivationPolicy)))
            + (canBuy ? t.Text("ui.price_estimate_note") : "")
            + (!string.IsNullOrWhiteSpace(package.Description) ? $"\n{Html(Limit(package.Description, 1000))}\n" : "")
            + t.Text("ui.compatibility_note")
            + t.Text(canBuy ? "ui.consent_note" : "ui.plan_unavailable_note");
        var rows = new List<InlineKeyboardButton[]>();
        rows.Add([SafeHttps(Sales.TermsUrl)
            ? InlineKeyboardButton.WithUrl(t.Text("ui.button_terms_sale"), Sales.TermsUrl)
            : Button(t.Text("ui.button_terms_sale"), "terms")]);
        if (canBuy)
            rows.Add([Button(t.Text("ui.button_agree"), $"agree:{key}:{Key(Sales.TermsVersion)}")]);
        rows.Add([Button(t.Text("ui.button_check_device"), "install"), Button(t.Text("ui.button_countries_back"), "countries:0")]);
        await ScreenAsync(userId, text, ct, new InlineKeyboardMarkup(rows), parseMode: ParseMode.Html);
    }

    private async Task InvoiceAsync(BotText t, long userId, Order order, CancellationToken ct)
    {
        if (order.UserId != userId || order.State != "quoted" || order.ExpiresAt <= DateTime.UtcNow)
        { await StaleAsync(t, userId, ct); return; }
        if (chatUi is not null) await chatUi.ClearAsync(userId, ct);
        var invoice = await bot.SendInvoice(userId, Limit(order.PackageName, 30),
            Limit(t.Text("ui.invoice_description", order.Id), 250),
            order.Id, "XTR", [new LabeledPrice(t.Text("ui.invoice_label"), order.Stars)], providerToken: "",
            startParameter: order.Id, protectContent: true, cancellationToken: ct);
        if (chatUi is not null) await chatUi.TrackAsync(userId, invoice.Id, replace: true, ct);
    }

    private async Task OrdersAsync(BotText t, long userId, int page, CancellationToken ct)
    {
        var orders = await shop.GetOrdersAsync(userId, page, ct);
        if (orders.Count == 0)
        {
            await ScreenAsync(userId, page == 0 ? t.Text("ui.no_orders") : t.Text("ui.no_more_orders"), ct,
                new InlineKeyboardMarkup(new[] { new[] { Button(page == 0 ? t.Text("ui.button_country") : t.Text("ui.button_back"), page == 0 ? "countries:0" : $"orders:{page - 1}") } })); return;
        }
        var rows = orders.Select(o => new[] { Button(Limit(t.Text("ui.order_button", State(t, o), o.PackageName), 55), $"order:{o.Id}") }).ToList();
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(Button("←", $"orders:{page - 1}"));
        if (orders.Count == OrderPageSize) navigation.Add(Button("→", $"orders:{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
        rows.Add([Button(t.Text("ui.button_menu"), "menu")]);
        await ScreenAsync(userId, t.Text("ui.orders_page", page + 1), ct, new(rows));
    }

    private async Task OrderAsync(BotText t, long userId, string id, CancellationToken ct)
    {
        var order = await shop.GetOrderAsync(userId, id, ct);
        if (order is null) { await StaleAsync(t, userId, ct); return; }
        var rows = new List<InlineKeyboardButton[]>();
        if (order.State == "quoted" && order.ExpiresAt > DateTime.UtcNow)
            rows.Add([Button(t.Text("ui.button_pay", order.Stars), $"pay:{order.Id}")]);
        if (order.State == "ready")
            rows.Add([Button(t.Text("ui.button_qr"), $"qr:{order.Id}"), Button(t.Text("ui.button_usage"), $"usage:{order.Id}")]);
        rows.Add([Button(t.Text("ui.button_refresh"), $"order:{order.Id}"), Button(t.Text("ui.button_orders_back"), "orders:0")]);
        await ScreenAsync(userId, t.Text("ui.order_details", order.Id, Limit(order.PackageName, 180), State(t, order), order.Stars, order.CreatedAt.ToString("yyyy-MM-dd HH:mm", t.Culture))
            + (order.State == "manual_review" ? t.Text("ui.manual_review_note") : ""), ct, new(rows));
    }

    private async Task UsageAsync(BotText t, long userId, string id, CancellationToken ct)
    {
        var order = await shop.GetOrderAsync(userId, id, ct);
        if (order is not { State: "ready" }) { await StaleAsync(t, userId, ct); return; }
        var usage = await shop.GetUsageAsync(userId, id, ct);
        await ScreenAsync(userId, usage is null ? t.Text("ui.usage_unavailable")
            : t.Text("ui.usage_details", Limit(order.PackageName, 100), Volume(t, usage.UsedBytes), Volume(t, usage.TotalBytes),
                Volume(t, Math.Max(0, usage.TotalBytes - usage.UsedBytes)), ProviderStatus(t, usage.Status))
                + (usage.ExpiresAt is { } expires ? t.Text("ui.usage_expires", expires.ToString("yyyy-MM-dd HH:mm", t.Culture)) : "")
                + t.Text("ui.usage_delay_note"), ct,
            new InlineKeyboardMarkup(new[] { new[] { Button(t.Text("ui.button_order_back"), $"order:{id}") } }));
    }

    private Task InstallAsync(BotText t, long userId, CancellationToken ct) => ScreenAsync(userId, t.Text("ui.install"), ct, MainKeyboard(t, userId));

    private async Task AdminStatisticsAsync(BotText t, long userId, CancellationToken ct)
    {
        if (!IsAdmin(userId)) { await MenuAsync(t, userId, ct); return; }
        var statistics = adminStatistics is null ? null : await adminStatistics.GetAsync(userId, ct);
        if (statistics is null)
        {
            await ScreenAsync(userId, t.Text("ui.admin_statistics_unavailable"), ct, MainKeyboard(t, userId));
            return;
        }

        var balance = statistics.ProviderBalanceUnits is { } units
            ? (units / 10000m).ToString("0.0000", t.Culture) + " USD"
            : t.Text("ui.admin_balance_unavailable");
        var text = t.Text("ui.admin_statistics",
            t.Text(statistics.BotReady ? "ui.admin_status_online" : "ui.admin_status_starting"),
            statistics.Users, statistics.Orders.Buyers, statistics.Orders.TotalOrders,
            statistics.Orders.OrdersLast24Hours, statistics.Orders.Quoted, statistics.Orders.Paid,
            statistics.Orders.Provisioning, statistics.Orders.Ready, statistics.Orders.Delivered,
            statistics.Orders.ManualReview, statistics.Payments.SuccessfulPayments,
            statistics.Payments.SuccessfulLast24Hours, statistics.Payments.Accepted,
            statistics.Payments.RefundPending, statistics.Payments.Refunded,
            statistics.Payments.GrossStars, statistics.Payments.RefundedStars, statistics.NetStars,
            statistics.IssuedEsims, balance,
            statistics.GeneratedAtUtc.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss", t.Culture),
            statistics.Payments.TotalPayments);
        await ScreenAsync(userId, text, ct, new InlineKeyboardMarkup(new[]
        {
            new[] { Button(t.Text("ui.button_refresh"), "adminstats") },
            new[] { Button(t.Text("ui.button_menu"), "menu") }
        }));
    }

    private async Task SupportAsync(BotText t, long userId, bool payment, CancellationToken ct)
    {
        if (support is not null) await support.BeginAsync(userId, payment, ct);
        else await ScreenAsync(userId, t.Text("support.unavailable"), ct, MainKeyboard(t, userId));
        if (SafeHttps(Sales.SupportUrl))
        {
            var sent = await bot.SendMessage(userId, t.Text("support.external_option"), replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl(t.Text("ui.button_contact_support"), Sales.SupportUrl) }
            }), protectContent: true, cancellationToken: ct);
            if (chatUi is not null) await chatUi.TrackAsync(userId, sent.Id, replace: false, ct);
        }
    }

    private Task TermsAsync(BotText t, long userId, CancellationToken ct) => ScreenAsync(userId,
        t.Text(SafeHttps(Sales.TermsUrl) ? "ui.terms" : "support.terms_builtin")
        + t.Text("ui.terms_version_note", Limit(Sales.TermsVersion, 80)), ct,
        SafeHttps(Sales.TermsUrl)
            ? new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithUrl(t.Text("ui.button_open_terms"), Sales.TermsUrl) }, new[] { Button(t.Text("ui.button_menu"), "menu") } })
            : MainKeyboard(t, userId));

    private async Task<bool> EnsureCatalogAsync(BotText t, long userId, CancellationToken ct)
    {
        if (shop.Readiness.CanBrowse) return true;
        await ScreenAsync(userId, t.Text("ui.catalog_missing"), ct, MainKeyboard(t, userId));
        return false;
    }

    private async Task<EsimPackage?> FindPackageAsync(string key, CancellationToken ct) => !shop.Readiness.CanBrowse
        ? null : (await shop.GetPackagesAsync(ct)).SingleOrDefault(p => Key(p.Code) == key);
    private Task StaleAsync(BotText t, long userId, CancellationToken ct) => ScreenAsync(userId, t.Text("ui.stale"), ct);

    private Task ScreenAsync(long userId, string text, CancellationToken ct,
        InlineKeyboardMarkup? keyboard = null, ParseMode parseMode = ParseMode.None) =>
        chatUi is null ? bot.SendMessage(userId, text, parseMode: parseMode, replyMarkup: keyboard, cancellationToken: ct)
            : chatUi.ShowAsync(userId, text, keyboard, parseMode, ct);
    private string Price(BotText t, EsimPackage package) => !shop.Readiness.CanBuy || !Sales.IsConfigured
        ? t.Text("ui.price_closed") : RetailStars(package) is { } stars
            ? t.Text("ui.price_stars_usd", stars, pricing.RetailUsdFor(package).ToString("0.00", CultureInfo.InvariantCulture))
            : t.Text("ui.price_unavailable");
    private static string DisplayCountry(BotText t, Country country) => Flag(country.Code) + " " + t.Country(country);
    private static string Flag(string code)
    {
        var upper = code.Trim().ToUpperInvariant();
        if (upper.Length != 2 || upper.Any(character => character is < 'A' or > 'Z')) return "🌐";
        return string.Concat(upper.Select(character => char.ConvertFromUtf32(0x1F1E6 + character - 'A')));
    }
    private int? RetailStars(EsimPackage package)
    {
        if (!Sales.IsConfigured) return null;
        try
        {
            var stars = pricing.StarsFor(package);
            return stars is > 0 and <= 100000 ? stars : null;
        }
        catch (Exception ex) when (ex is OverflowException or InvalidOperationException) { return null; }
    }
    private bool IsAdmin(long userId) => userId > 0 && userId == botOptions.Value.AdminUserId;
    private static InlineKeyboardButton Button(string text, string data) => InlineKeyboardButton.WithCallbackData(text, data);
    private BotText FallbackText(string? telegramLanguageCode)
    {
        var code = telegramLanguageCode?.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return new BotText(code is not null && BotText.SupportedCodes.Contains(code) ? code : botOptions.Value.DefaultLanguage);
    }
    private static string Html(string text) => WebUtility.HtmlEncode(text);
    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max] + "…";
    internal static string Key(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..20];
    private static int Page(string value) => int.TryParse(value, out var page) ? Math.Clamp(page, 0, 10000) : 0;
    private static bool Matches(string value, string query) => SearchComparer.IndexOf(value, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    private static bool SafeHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo);
    internal static string Volume(BotText t, long bytes) => bytes >= 1024L * 1024 * 1024
        ? t.Text("ui.volume_gb", (bytes / (1024m * 1024 * 1024)).ToString("0.##", t.Culture))
        : t.Text("ui.volume_mb", (Math.Max(0, bytes) / (1024m * 1024)).ToString("0.##", t.Culture));
    private static string Duration(BotText t, EsimPackage p) => p.DurationUnit.ToUpperInvariant() switch
    {
        "DAY" or "DAYS" => t.Text("ui.duration_days", p.Duration),
        "HOUR" or "HOURS" => t.Text("ui.duration_hours", p.Duration),
        _ => t.Text("ui.duration_other", p.Duration, Limit(p.DurationUnit, 20))
    };
    private static string State(BotText t, Order o) => t.Text(o.State switch
    {
        "quoted" => o.ExpiresAt <= DateTime.UtcNow ? "ui.state_expired" : "ui.state_quoted",
        "paid" => "ui.state_paid", "provisioning" => "ui.state_provisioning", "ready" => "ui.state_ready",
        "refund_pending" => "ui.state_refund_pending", "refunded" => "ui.state_refunded", "manual_review" => "ui.state_manual_review",
        _ => "ui.state_unknown"
    });
    private static string ProviderStatus(BotText t, string status) => t.Text(status switch
    {
        "CREATE" or "PAYING" or "PAID" or "GETTING_RESOURCE" => "ui.provider_waiting",
        "GOT_RESOURCE" or "UNUSED" => "ui.provider_ready",
        "IN_USE" => "ui.provider_active",
        "USED_UP" or "EXPIRED" => "ui.provider_finished",
        "CANCEL" or "CANCELLED" or "RELEASED" => "ui.provider_cancelled",
        _ => "ui.provider_unknown"
    });
    private static void AddPager(List<InlineKeyboardButton[]> rows, string prefix, int page, int count, int size)
    {
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(Button("←", $"{prefix}:{page - 1}"));
        if ((page + 1) * size < count) navigation.Add(Button("→", $"{prefix}:{page + 1}"));
        if (navigation.Count > 0) rows.Add(navigation.ToArray());
    }

}

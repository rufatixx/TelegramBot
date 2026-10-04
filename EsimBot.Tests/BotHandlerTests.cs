using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;

namespace EsimBot.Tests;

public sealed class BotHandlerTests
{
    [Fact]
    public async Task Statistics_button_and_real_dashboard_are_visible_only_to_the_configured_admin()
    {
        var customerUi = new FakeChatUi();
        using (var customer = new Fixture(chatUi: customerUi, adminId: 99, adminStatistics: new FakeAdminStatistics()))
        {
            await customer.Handler.HandleAsync(Message("/start"), default);
            Assert.DoesNotContain("adminstats", customerUi.Keyboards.Single());
            await customer.Handler.HandleAsync(Callback("adminstats"), default);
            Assert.DoesNotContain("Статистика RoamiSIM", customerUi.Screens[^1].Text);
        }

        var adminUi = new FakeChatUi();
        var statistics = new FakeAdminStatistics();
        using var admin = new Fixture(chatUi: adminUi, adminId: 42, adminStatistics: statistics);
        await admin.Handler.HandleAsync(Message("/start"), default);
        Assert.Contains("adminstats", adminUi.Keyboards.Single());
        await admin.Handler.HandleAsync(Callback("adminstats"), default);
        Assert.Equal([42L], statistics.Requests);
        Assert.Contains("Пользователей: 120", adminUi.Screens[^1].Text);
        Assert.Contains("Успешных: 9", adminUi.Screens[^1].Text);
        Assert.Contains("Баланс поставщика: 12,3456 USD", adminUi.Screens[^1].Text);
    }

    [Fact]
    public async Task Command_updates_single_app_screen_and_removes_the_customer_input()
    {
        var ui = new FakeChatUi();
        using var fixture = new Fixture(chatUi: ui);
        await fixture.Handler.HandleAsync(Message("/catalog"), default);
        Assert.Equal([(42L, 1)], ui.Dismissed);
        Assert.Single(ui.Screens);
        Assert.Contains("Страница", ui.Screens[0].Text);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task Country_buttons_and_plan_heading_show_the_country_flag()
    {
        var ui = new FakeChatUi();
        using var fixture = new Fixture(chatUi: ui);
        await fixture.Handler.HandleAsync(Message("/catalog"), default);
        using var keyboard = JsonDocument.Parse(ui.Keyboards.Single());
        Assert.Equal("🇹🇷 Турция", keyboard.RootElement.GetProperty("inline_keyboard")[0][0].GetProperty("text").GetString());
        await fixture.Handler.HandleAsync(Callback($"country:{Key("TR")}:0"), default);
        Assert.Contains("🇹🇷 Турция", ui.Screens[^1].Text);
    }

    [Fact]
    public async Task Callback_edits_its_own_app_screen_without_deleting_any_message()
    {
        var ui = new FakeChatUi();
        using var fixture = new Fixture(chatUi: ui);
        await fixture.Handler.HandleAsync(Callback("countries:0"), default);
        Assert.Equal([1], ui.Contexts);
        Assert.Empty(ui.Dismissed);
        Assert.Single(ui.Screens);
        Assert.Single(fixture.Http.Requests, request => request.Method == "answerCallbackQuery");
    }

    [Fact]
    public async Task Support_conversation_stays_as_separate_messages_and_is_not_dismissed()
    {
        var support = new FakeSupport { ConsumeMessages = true };
        var ui = new FakeChatUi();
        using var fixture = new Fixture(support: support, chatUi: ui);
        await fixture.Handler.HandleAsync(Message("Мне нужна помощь"), default);
        Assert.Single(support.Messages);
        Assert.Empty(ui.Dismissed);
        Assert.Empty(ui.Screens);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("payment")]
    [InlineData("refund")]
    [InlineData("checkout")]
    public async Task Test_environment_routes_every_update_away_from_the_production_shop(string kind)
    {
        var sandbox = new TestPayments();
        using var fixture = new Fixture(true, sandbox);
        var update = kind switch
        {
            "payment" => PaymentUpdate(), "refund" => RefundUpdate(), "checkout" => CheckoutUpdate(),
            _ => Message("/catalog")
        };
        await fixture.Handler.HandleAsync(update, default);
        Assert.Same(update, Assert.Single(sandbox.Updates));
        Assert.Empty(fixture.Http.Requests);
        Assert.Null(fixture.Shop.Payment);
        Assert.Null(fixture.Shop.RecordedRefund);
        Assert.Equal(0, fixture.Shop.CatalogueReads);
    }

    [Fact]
    public async Task Missing_test_service_fails_closed_without_falling_back_to_production()
    {
        using var fixture = new Fixture(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.HandleAsync(PaymentUpdate(), default));
        Assert.Null(fixture.Shop.Payment);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task Production_mode_does_not_invoke_test_service()
    {
        var sandbox = new TestPayments();
        using var fixture = new Fixture(false, sandbox);
        await fixture.Handler.HandleAsync(PaymentUpdate(), default);
        Assert.Empty(sandbox.Updates);
        Assert.NotNull(fixture.Shop.Payment);
    }

    [Fact]
    public async Task MissingProviderHasNoFabricatedCatalogue()
    {
        using var fixture = new Fixture();
        fixture.Shop.Readiness = new(false, false);
        await fixture.Handler.HandleAsync(Message("/catalog"), default);
        Assert.Equal(0, fixture.Shop.CatalogueReads);
        Assert.Contains("ещё не подключён", fixture.Http.Requests.Single().Body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task OrdinaryGroupMessagesAreIgnored()
    {
        using var fixture = new Fixture();
        var update = Message("/catalog");
        update.Message!.Chat.Type = ChatType.Group;
        update.Message.Chat.Id = -100;
        await fixture.Handler.HandleAsync(update, default);
        Assert.Empty(fixture.Http.Requests);
        Assert.Equal(0, fixture.Shop.CatalogueReads);
    }

    [Fact]
    public async Task ForeignPaymentIsRecordedWithActualPayerBeforeRefundReply()
    {
        using var fixture = new Fixture();
        fixture.Shop.PaymentResult = new(false, false, true);
        var update = PaymentUpdate();
        // Even an unexpected group payment is accounted for, with a private reply.
        update.Message!.Chat.Type = ChatType.Group;
        update.Message.Chat.Id = -100;
        await fixture.Handler.HandleAsync(update, default);
        Assert.Equal((42L, "someone-elses-order", "telegram-charge", "XTR", 100), fixture.Shop.Payment);
        var reply = fixture.Http.Requests.Single().Body;
        Assert.Equal(42, reply.GetProperty("chat_id").GetInt64());
        Assert.Contains("Возврат", reply.GetProperty("text").GetString());
    }

    [Fact]
    public async Task PaymentPersistenceFailureIsNotSilentlyAcknowledged()
    {
        using var fixture = new Fixture();
        fixture.Shop.PaymentFailure = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.HandleAsync(PaymentUpdate(), default));
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedFinancialUpdateIsHandledWhenCustomerBlockedBot(bool refund)
    {
        using var fixture = new Fixture();
        fixture.Http.FailureMethod = "sendMessage";
        fixture.Http.FailureCode = 403;
        fixture.Http.FailureDescription = "Forbidden: bot was blocked by the user";
        await fixture.Handler.HandleAsync(refund ? RefundUpdate() : PaymentUpdate(), default);
        if (refund) Assert.Equal((42L, "charge-1"), fixture.Shop.RecordedRefund);
        else Assert.NotNull(fixture.Shop.Payment);
        Assert.Single(fixture.Http.Requests);
    }

    [Fact]
    public async Task RefundPersistenceFailureStillPropagatesWithoutNotification()
    {
        using var fixture = new Fixture();
        fixture.Shop.RefundFailure = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.HandleAsync(RefundUpdate(), default));
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task RefundUsesPrivateChatOwnerEvenWhenServiceMessageIsFromBot()
    {
        using var fixture = new Fixture();
        var update = RefundUpdate();
        update.Message!.From = new User { Id = 123456, IsBot = true, FirstName = "Shop" };
        await fixture.Handler.HandleAsync(update, default);
        Assert.Equal((42L, "charge-1"), fixture.Shop.RecordedRefund);
        Assert.Equal(42, fixture.Http.Requests.Single().Body.GetProperty("chat_id").GetInt64());
    }

    [Fact]
    public async Task RejectedCheckoutAnswersTelegramWithoutApprovingCharge()
    {
        using var fixture = new Fixture();
        fixture.Shop.CheckoutError = "Заказ принадлежит другому пользователю.";
        await fixture.Handler.HandleAsync(new Update
        {
            PreCheckoutQuery = new PreCheckoutQuery
            {
                Id = "checkout", From = Buyer(), Currency = "XTR", TotalAmount = 100, InvoicePayload = "foreign-order"
            }
        }, default);
        var response = fixture.Http.Requests.Single();
        Assert.Equal("answerPreCheckoutQuery", response.Method);
        Assert.False(response.Body.GetProperty("ok").GetBoolean());
        Assert.Equal(fixture.Shop.CheckoutError, response.Body.GetProperty("error_message").GetString());
    }

    [Theory]
    [InlineData("Bad Request: query is too old and response timeout expired or query ID is invalid")]
    [InlineData("Bad Request: QUERY_ID_INVALID")]
    [InlineData("Bad Request: QUERY_ALREADY_ANSWERED")]
    public async Task ExpiredCheckoutIsHandledSoLaterPaymentsCanBeAcknowledged(string description)
    {
        using var fixture = new Fixture();
        fixture.Http.FailureMethod = "answerPreCheckoutQuery";
        fixture.Http.FailureCode = 400;
        fixture.Http.FailureDescription = description;
        await fixture.Handler.HandleAsync(CheckoutUpdate(), default);
        Assert.Single(fixture.Http.Requests);
    }

    [Theory]
    [InlineData(400, "Bad Request: invalid parameter")]
    [InlineData(500, "Internal Server Error")]
    public async Task OtherCheckoutTransportErrorsRemainRetryable(int code, string description)
    {
        using var fixture = new Fixture();
        fixture.Http.FailureMethod = "answerPreCheckoutQuery";
        fixture.Http.FailureCode = code;
        fixture.Http.FailureDescription = description;
        await Assert.ThrowsAsync<ApiRequestException>(() => fixture.Handler.HandleAsync(CheckoutUpdate(), default));
    }

    [Fact]
    public async Task NonAdminCannotRequestRefund()
    {
        using var fixture = new Fixture();
        await fixture.Handler.HandleAsync(Message("/refund order-1"), default);
        Assert.Equal(0, fixture.Shop.RefundRequests);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task DetailsRequireExplicitConsentAndNeverDisplayWholesaleCost()
    {
        using var fixture = new Fixture();
        fixture.Shop.Readiness = new(true, false);
        await fixture.Handler.HandleAsync(Callback($"package:{Key(fixture.Shop.Packages[0].Code)}"), default);
        Assert.Equal(0, fixture.Shop.Quotes);
        var message = fixture.Http.Requests.Single(r => r.Method == "sendMessage").Body;
        Assert.Contains("продажи закрыты", message.GetProperty("text").GetString());
        Assert.DoesNotContain("12.345", message.GetProperty("text").GetString());
        Assert.DoesNotContain("USD", message.GetProperty("text").GetString());
        Assert.DoesNotContain("agree:", message.GetProperty("reply_markup").GetRawText());
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("az")]
    [InlineData("en")]
    public async Task Available_plan_shows_stars_and_retail_usd_reference_with_localized_explanation(string language)
    {
        using var fixture = new Fixture();
        fixture.Languages.Preferences[42] = language;
        await fixture.Handler.HandleAsync(Callback($"package:{Key(fixture.Shop.Packages[0].Code)}"), default);
        var text = fixture.Http.Requests.Single(r => r.Method == "sendMessage").Body.GetProperty("text").GetString();
        Assert.Contains("1358 ⭐ ≈ $13.58", text);
        Assert.Contains(new BotText(language).Text("ui.price_estimate_note"), text);
        Assert.DoesNotContain("12.345", text);
    }

    [Fact]
    public async Task ConsentProducesOwnedSingleUseStarsInvoice()
    {
        using var fixture = new Fixture();
        await fixture.Handler.HandleAsync(Callback($"agree:{Key(fixture.Shop.Packages[0].Code)}:{Key("v1")}"), default);
        Assert.Equal(1, fixture.Shop.Quotes);
        var invoice = fixture.Http.Requests.Single(r => r.Method == "sendInvoice").Body;
        Assert.Equal(42, invoice.GetProperty("chat_id").GetInt64());
        Assert.Equal("XTR", invoice.GetProperty("currency").GetString());
        Assert.Equal("order-1", invoice.GetProperty("payload").GetString());
        Assert.Equal("order-1", invoice.GetProperty("start_parameter").GetString());
        Assert.True(invoice.GetProperty("protect_content").GetBoolean());
        Assert.Equal(1, invoice.GetProperty("prices").GetArrayLength());
        Assert.Equal(200, invoice.GetProperty("prices")[0].GetProperty("amount").GetInt32());
    }

    [Fact]
    public async Task Invoice_replaces_the_current_screen_and_is_tracked_for_later_removal()
    {
        var ui = new FakeChatUi();
        using var fixture = new Fixture(chatUi: ui);
        await fixture.Handler.HandleAsync(Callback($"agree:{Key(fixture.Shop.Packages[0].Code)}:{Key("v1")}"), default);
        Assert.Equal([42L], ui.Cleared);
        Assert.Equal([(42L, 1, true)], ui.Tracked);
    }

    [Fact]
    public async Task Persisted_payment_deletes_the_service_message_and_replaces_the_invoice_with_receipt()
    {
        var ui = new FakeChatUi();
        using var fixture = new Fixture(chatUi: ui);
        await fixture.Handler.HandleAsync(PaymentUpdate(), default);
        Assert.Equal([(42L, 1)], ui.Dismissed);
        Assert.Contains("Оплата получена", ui.Screens.Single().Text);
    }

    [Fact]
    public async Task ChangedTermsRejectOldConsentBeforeCreatingOrder()
    {
        using var fixture = new Fixture();
        await fixture.Handler.HandleAsync(Callback($"agree:{Key(fixture.Shop.Packages[0].Code)}:{Key("old-terms")}"), default);
        Assert.Equal(0, fixture.Shop.Quotes);
        Assert.DoesNotContain(fixture.Http.Requests, r => r.Method == "sendInvoice");
    }

    [Fact]
    public async Task ProviderLongPackageCodeDoesNotExceedTelegramCallbackLimit()
    {
        using var fixture = new Fixture();
        await fixture.Handler.HandleAsync(Callback($"country:{Key("TR")}:0"), default);
        var message = fixture.Http.Requests.Single(r => r.Method == "sendMessage").Body;
        var callbacks = message.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray()
            .SelectMany(row => row.EnumerateArray()).Select(button => button.GetProperty("callback_data").GetString()!).ToArray();
        Assert.Contains(callbacks, value => value.StartsWith("package:"));
        Assert.All(callbacks, value => Assert.InRange(Encoding.UTF8.GetByteCount(value), 1, 64));
    }

    [Fact]
    public async Task ForeignOrderCannotExposeProfile()
    {
        using var fixture = new Fixture();
        await fixture.Handler.HandleAsync(Callback("qr:foreign-order"), default);
        Assert.Equal((42L, "foreign-order"), fixture.Shop.OrderLookup);
        Assert.Equal(0, fixture.Shop.ProfileReads);
        Assert.DoesNotContain(fixture.Http.Requests, r => r.Method == "sendPhoto");
    }

    [Fact]
    public async Task DeliveryGeneratesLocalPngAndProtectsEscapedActivationData()
    {
        using var fixture = new Fixture();
        var order = new Order("order-1", 42, "package", "Turkey", 10000, 200, "ready", "provider-order",
            DateTime.UtcNow, DateTime.UtcNow, 0, null);
        await fixture.Notifier.DeliverAsync(order,
            new StoredProfile("profile", "iccid", "LPA:1$rsp.example.test$A<B&C", "internet"), default);
        var photo = fixture.Http.Requests.Single(r => r.Method == "sendPhoto").Body;
        Assert.Equal(42, photo.GetProperty("chat_id").GetInt64());
        Assert.True(photo.GetProperty("protect_content").GetBoolean());
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, fixture.Http.Uploads.Single().Value[..8]);
        var manual = fixture.Http.Requests.Single(r => r.Method == "sendMessage").Body;
        Assert.True(manual.GetProperty("protect_content").GetBoolean());
        Assert.Contains("<code>A&lt;B&amp;C</code>", manual.GetProperty("text").GetString());
        Assert.Equal(2, fixture.Http.Requests.Count);
    }

    [Fact]
    public async Task Qr_and_installation_messages_replace_the_old_screen_and_are_both_tracked()
    {
        var ui = new FakeChatUi();
        using var fixture = new Fixture(chatUi: ui);
        var order = new Order("order-1", 42, "package", "Turkey", 10000, 200, "ready", "provider-order",
            DateTime.UtcNow, DateTime.UtcNow, 0, null);
        await fixture.Notifier.DeliverAsync(order,
            new StoredProfile("profile", "iccid", "LPA:1$rsp.example.test$activation", "internet"), default);
        Assert.Equal([42L], ui.Cleared);
        Assert.Equal([(42L, 1, true), (42L, 1, false)], ui.Tracked);
    }

    [Fact]
    public async Task MissingActivationCodeIsNotReportedAsDelivered()
    {
        using var fixture = new Fixture();
        var order = new Order("order-1", 42, "package", "Turkey", 10000, 200, "ready", "provider-order",
            DateTime.UtcNow, DateTime.UtcNow, 0, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Notifier.DeliverAsync(order,
            new StoredProfile("profile", "iccid", "", null), default));
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData("az")]
    [InlineData("en")]
    public async Task LanguageChoicePersistsAndUpdatesChatCommands(string code)
    {
        using var fixture = new Fixture();
        await fixture.Handler.HandleAsync(Callback($"lang:{code}"), default);
        Assert.Equal(code, fixture.Languages.Preferences[42]);
        var context = new BotText(code);
        var receipt = fixture.Http.Requests.Single(r => r.Method == "sendMessage").Body;
        Assert.Equal(context.Text("ui.language_saved"), receipt.GetProperty("text").GetString());
        var commands = fixture.Http.Requests.Single(r => r.Method == "setMyCommands").Body;
        Assert.Equal(42, commands.GetProperty("scope").GetProperty("chat_id").GetInt64());
        Assert.Equal(context.Text("ui.command_start"), commands.GetProperty("commands")[0].GetProperty("description").GetString());
        await fixture.Handler.HandleAsync(Message("/start"), default);
        Assert.Contains(fixture.Http.Requests, r => r.Method == "sendMessage"
            && r.Body.GetProperty("text").GetString()!.StartsWith(context.Text("ui.welcome"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentUsersKeepIndependentLanguages()
    {
        using var fixture = new Fixture();
        var english = Message("/start");
        english.Message!.From!.LanguageCode = "en-US";
        var azerbaijani = Message("/start");
        azerbaijani.Message!.From!.Id = 43;
        azerbaijani.Message.From.LanguageCode = "az";
        azerbaijani.Message.Chat.Id = 43;
        await Task.WhenAll(fixture.Handler.HandleAsync(english, default), fixture.Handler.HandleAsync(azerbaijani, default));
        foreach (var (id, code) in new[] { (42L, "en"), (43L, "az") })
        {
            var response = fixture.Http.Requests.Single(r => r.Method == "sendMessage" && r.Body.GetProperty("chat_id").GetInt64() == id).Body;
            Assert.StartsWith(new BotText(code).Text("ui.welcome"), response.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task CountrySearchAcceptsAnotherLanguageWithoutChangingSelectedLanguage()
    {
        using var fixture = new Fixture();
        fixture.Languages.Preferences[42] = "en";
        await fixture.Handler.HandleAsync(Message("Турция"), default);
        var response = fixture.Http.Requests.Single(r => r.Method == "sendMessage").Body.GetProperty("text").GetString();
        Assert.Contains("Plans from lowest to highest price", response);
        Assert.Contains(new BotText("en").Country(new Country("TR", "Turkey")), response);
    }

    [Fact]
    public async Task CheckoutBusinessErrorIsLocalized()
    {
        using var fixture = new Fixture();
        fixture.Languages.Preferences[42] = "en";
        fixture.Shop.CheckoutError = "ui.checkout_expired";
        await fixture.Handler.HandleAsync(CheckoutUpdate(), default);
        var response = fixture.Http.Requests.Single().Body;
        Assert.Equal(new BotText("en").Text("ui.checkout_expired"), response.GetProperty("error_message").GetString());
    }

    [Fact]
    public async Task LanguageLookupFailureAfterPaymentDoesNotBlockItsAcknowledgement()
    {
        using var fixture = new Fixture();
        fixture.Languages.FailReads = true;
        await fixture.Handler.HandleAsync(PaymentUpdate(), default);
        Assert.NotNull(fixture.Shop.Payment);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task FailedLanguageLookupRejectsCheckoutInKnownTelegramLanguage()
    {
        using var fixture = new Fixture();
        fixture.Languages.FailReads = true;
        var update = CheckoutUpdate();
        update.PreCheckoutQuery!.From.LanguageCode = "az-AZ";
        await fixture.Handler.HandleAsync(update, default);
        var response = fixture.Http.Requests.Single().Body;
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal(new BotText("az").Text("ui.checkout_unavailable"), response.GetProperty("error_message").GetString());
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("az")]
    [InlineData("en")]
    public async Task Builtin_terms_are_available_without_external_site(string language)
    {
        using var fixture = new Fixture(builtinTerms: true);
        fixture.Languages.Preferences[42] = language;
        await fixture.Handler.HandleAsync(Message("/terms"), default);
        var response = Assert.Single(fixture.Http.Requests).Body;
        Assert.Equal(new BotText(language).Text("support.terms_builtin")
            + new BotText(language).Text("ui.terms_version_note", fixture.Sales.TermsVersion), response.GetProperty("text").GetString());
        Assert.DoesNotContain(Buttons(response), button => button.TryGetProperty("url", out _));
    }

    [Fact]
    public async Task Package_always_links_to_builtin_terms_before_consent_when_external_url_is_missing()
    {
        using var fixture = new Fixture(builtinTerms: true);
        await fixture.Handler.HandleAsync(Callback("package:" + Key(fixture.Shop.Packages[0].Code)), default);
        var response = Assert.Single(fixture.Http.Requests, r => r.Method == "sendMessage").Body;
        var buttons = Buttons(response).ToArray();
        Assert.Contains(buttons, button => button.TryGetProperty("callback_data", out var data) && data.GetString() == "terms");
        Assert.Contains(buttons, button => button.TryGetProperty("callback_data", out var data) && data.GetString()!.StartsWith("agree:"));
    }

    [Fact]
    public async Task External_terms_override_is_retained()
    {
        using var fixture = new Fixture();
        await fixture.Handler.HandleAsync(Message("/terms"), default);
        var response = Assert.Single(fixture.Http.Requests).Body;
        Assert.StartsWith(new BotText("ru").Text("ui.terms"), response.GetProperty("text").GetString());
        Assert.Contains(Buttons(response), button => button.TryGetProperty("url", out var url) && url.GetString() == fixture.Sales.TermsUrl);
    }

    [Theory]
    [InlineData("/support", false)]
    [InlineData("/paysupport", true)]
    public async Task Support_commands_start_opt_in_relay_without_external_contact(string command, bool payment)
    {
        var support = new FakeSupport();
        using var fixture = new Fixture(support: support, builtinTerms: true);
        await fixture.Handler.HandleAsync(Message(command), default);
        Assert.Equal((42L, payment), Assert.Single(support.Starts));
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task Support_deep_link_opens_support_directly()
    {
        var support = new FakeSupport();
        using var fixture = new Fixture(support: support, builtinTerms: true);
        await fixture.Handler.HandleAsync(Message("/start support"), default);
        Assert.Equal((42L, false), Assert.Single(support.Starts));
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task Terms_deep_link_shows_purchase_terms_directly()
    {
        using var fixture = new Fixture(builtinTerms: true);
        await fixture.Handler.HandleAsync(Message("/start terms"), default);
        Assert.StartsWith(new BotText("ru").Text("support.terms_builtin"),
            Assert.Single(fixture.Http.Requests).Body.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("invoice_123")]
    [InlineData("support extra")]
    public async Task Unknown_start_payloads_keep_the_main_menu(string payload)
    {
        var support = new FakeSupport();
        using var fixture = new Fixture(support: support, builtinTerms: true);
        await fixture.Handler.HandleAsync(Message("/start " + payload), default);
        Assert.Empty(support.Starts);
        Assert.StartsWith(new BotText("ru").Text("ui.welcome"),
            Assert.Single(fixture.Http.Requests).Body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Support_menu_button_starts_opt_in_relay_and_acknowledges_the_button()
    {
        var support = new FakeSupport();
        using var fixture = new Fixture(support: support, builtinTerms: true);
        await fixture.Handler.HandleAsync(Callback("support"), default);
        Assert.Equal((42L, false), Assert.Single(support.Starts));
        Assert.Equal(42, Assert.Single(support.Cancellations));
        Assert.Single(fixture.Http.Requests);
    }

    [Fact]
    public async Task Support_message_is_consumed_before_country_search_and_navigation_cancels_mode()
    {
        var support = new FakeSupport { ConsumeMessages = true };
        using var fixture = new Fixture(support: support);
        await fixture.Handler.HandleAsync(Message("my voluntary ticket"), default);
        Assert.Single(support.Messages);
        Assert.Equal(0, fixture.Shop.CatalogueReads);
        Assert.Empty(fixture.Http.Requests);
        await fixture.Handler.HandleAsync(Callback("menu"), default);
        Assert.Equal(42, Assert.Single(support.Cancellations));
    }

    [Theory]
    [InlineData("payment")]
    [InlineData("refund")]
    [InlineData("checkout")]
    public async Task Financial_updates_never_enter_support_routing(string kind)
    {
        var support = new FakeSupport { ConsumeMessages = true };
        using var fixture = new Fixture(support: support);
        await fixture.Handler.HandleAsync(kind switch
        {
            "payment" => PaymentUpdate(), "refund" => RefundUpdate(), _ => CheckoutUpdate()
        }, default);
        Assert.Empty(support.Messages);
        Assert.Empty(support.Cancellations);
        Assert.Single(fixture.Http.Requests);
    }

    private static IEnumerable<JsonElement> Buttons(JsonElement body) => body.GetProperty("reply_markup")
        .GetProperty("inline_keyboard").EnumerateArray().SelectMany(row => row.EnumerateArray());

    private static User Buyer() => new() { Id = 42, FirstName = "Customer" };
    private static Update Message(string text) => new()
    {
        Message = new Message { Id = 1, Chat = new Chat { Id = 42, Type = ChatType.Private }, From = Buyer(), Text = text }
    };
    private static Update Callback(string data) => new()
    {
        CallbackQuery = new CallbackQuery { Id = "callback-id", From = Buyer(), Data = data, Message = Message("test").Message! }
    };
    private static Update PaymentUpdate()
    {
        var update = Message("");
        update.Message!.SuccessfulPayment = new SuccessfulPayment
        {
            Currency = "XTR", TotalAmount = 100, InvoicePayload = "someone-elses-order",
            TelegramPaymentChargeId = "telegram-charge", ProviderPaymentChargeId = ""
        };
        return update;
    }
    private static Update RefundUpdate()
    {
        var update = Message("");
        update.Message!.RefundedPayment = new RefundedPayment
        {
            Currency = "XTR", TotalAmount = 100, InvoicePayload = "order-1", TelegramPaymentChargeId = "charge-1"
        };
        return update;
    }
    private static Update CheckoutUpdate() => new()
    {
        PreCheckoutQuery = new PreCheckoutQuery
        {
            Id = "checkout", From = Buyer(), Currency = "XTR", TotalAmount = 100, InvoicePayload = "order-1"
        }
    };
    private static string Key(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];

    private sealed class Fixture : IDisposable
    {
        public FakeShop Shop { get; } = new();
        public FakeLanguages Languages { get; } = new();
        public TelegramHttp Http { get; } = new();
        public BotHandler Handler { get; }
        public BotNotifier Notifier { get; }
        public SalesOptions Sales { get; }
        private readonly HttpClient _httpClient;
        public Fixture(bool testing = false, IPaymentTestService? paymentTests = null, ISupportService? support = null,
            bool builtinTerms = false, IChatUiService? chatUi = null, long adminId = 99,
            IAdminStatisticsService? adminStatistics = null)
        {
            _httpClient = new HttpClient(Http);
            var client = new TelegramBotClient("123456:TEST_TOKEN_NOT_A_REAL_SECRET", _httpClient);
            Sales = new SalesOptions
            {
                Enabled = true, NetUsdPerStar = 0.01m,
                SupportUrl = builtinTerms ? "" : "https://example.test/support",
                TermsUrl = builtinTerms ? "" : "https://example.test/terms", TermsVersion = "v1"
            };
            var sales = Options.Create(Sales);
            var botOptions = Options.Create(new BotOptions { AdminUserId = adminId, DefaultLanguage = "ru", TestEnvironment = testing });
            Notifier = new BotNotifier(client, botOptions, Languages, chatUi);
            Handler = new BotHandler(client, Shop, Notifier, new Pricing(sales), sales,
                botOptions, NullLogger<BotHandler>.Instance, Languages, paymentTests, support, chatUi, adminStatistics);
        }
        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class FakeSupport : ISupportService
    {
        public bool ConsumeMessages { get; init; }
        public List<(long, bool)> Starts { get; } = [];
        public List<Telegram.Bot.Types.Message> Messages { get; } = [];
        public List<long> Cancellations { get; } = [];
        public Task BeginAsync(long userId, bool payment, CancellationToken ct)
        { Starts.Add((userId, payment)); return Task.CompletedTask; }
        public Task<bool> HandleAsync(Telegram.Bot.Types.Message message, CancellationToken ct)
        { Messages.Add(message); return Task.FromResult(ConsumeMessages); }
        public Task CancelAsync(long userId, CancellationToken ct)
        { Cancellations.Add(userId); return Task.CompletedTask; }
    }

    private sealed class FakeChatUi : IChatUiService
    {
        public List<int?> Contexts { get; } = [];
        public List<(long UserId, string Text)> Screens { get; } = [];
        public List<(long UserId, int MessageId)> Dismissed { get; } = [];
        public List<long> Cleared { get; } = [];
        public List<(long UserId, int MessageId, bool Replace)> Tracked { get; } = [];
        public List<string> Keyboards { get; } = [];
        public IDisposable BeginScreen(int? messageId)
        {
            Contexts.Add(messageId);
            return new EmptyScope();
        }
        public Task ShowAsync(long userId, string text, InlineKeyboardMarkup? keyboard, ParseMode parseMode, CancellationToken ct)
        {
            Screens.Add((userId, text));
            Keyboards.Add(keyboard is null ? "" : JsonSerializer.Serialize(keyboard, JsonBotAPI.Options));
            return Task.CompletedTask;
        }
        public Task DismissInputAsync(long userId, int messageId, CancellationToken ct)
        { Dismissed.Add((userId, messageId)); return Task.CompletedTask; }
        public Task ClearAsync(long userId, CancellationToken ct)
        { Cleared.Add(userId); return Task.CompletedTask; }
        public Task TrackAsync(long userId, int messageId, bool replace, CancellationToken ct)
        { Tracked.Add((userId, messageId, replace)); return Task.CompletedTask; }
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }

    private sealed class TestPayments : IPaymentTestService
    {
        public List<Update> Updates { get; } = [];
        public Task HandleAsync(Update update, CancellationToken ct)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdminStatistics : IAdminStatisticsService
    {
        public List<long> Requests { get; } = [];
        public Task<AdminStatistics?> GetAsync(long adminUserId, CancellationToken ct)
        {
            Requests.Add(adminUserId);
            return Task.FromResult<AdminStatistics?>(new AdminStatistics(120,
                new OrderStatistics(7, 10, 2, 1, 1, 1, 5, 4, 0, 1, 1),
                new PaymentStatistics(11, 9, 2, 7, 1, 1, 90, 10),
                5, 123456, true, new DateTime(2026, 10, 4, 18, 0, 0, DateTimeKind.Utc)));
        }
    }

    private sealed class TelegramHttp : HttpMessageHandler
    {
        public ConcurrentBag<(string Method, JsonElement Body)> Requests { get; } = [];
        public Dictionary<string, byte[]> Uploads { get; } = new();
        public string? FailureMethod { get; set; }
        public int FailureCode { get; set; }
        public string FailureDescription { get; set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.Segments.Last();
            if (request.Content is MultipartFormDataContent form)
            {
                var values = new Dictionary<string, object?>();
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (part.Headers.ContentDisposition.FileName is not null)
                    {
                        Uploads[name] = await part.ReadAsByteArrayAsync(cancellationToken);
                        values[name] = "<uploaded file>";
                    }
                    else
                    {
                        var value = await part.ReadAsStringAsync(cancellationToken);
                        if (bool.TryParse(value, out var boolean)) values[name] = boolean;
                        else
                        {
                            try { values[name] = JsonSerializer.Deserialize<JsonElement>(value); }
                            catch (JsonException) { values[name] = value; }
                        }
                    }
                }
                Requests.Add((method, JsonSerializer.SerializeToElement(values)));
            }
            else
            {
                var text = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(text);
                Requests.Add((method, document.RootElement.Clone()));
            }
            if (method == FailureMethod)
                return new HttpResponseMessage((HttpStatusCode)FailureCode)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        ok = false, error_code = FailureCode, description = FailureDescription
                    }), Encoding.UTF8, "application/json")
                };
            var result = method.StartsWith("answer", StringComparison.Ordinal) || method == "setMyCommands" ? "true"
                : "{\"message_id\":1,\"date\":0,\"chat\":{\"id\":42,\"type\":\"private\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeShop : IShopService
    {
        public ShopReadiness Readiness { get; set; } = new(true, true);
        public IReadOnlyList<EsimPackage> Packages { get; } = new[]
        {
            new EsimPackage(new string('P', 200), "Turkey 1 GB", 123450, "USD", 1073741824, 7, "DAY",
                [new Country("TR", "Turkey")], "Mobile internet", "first_connection", "4G/5G", "1")
        };
        public int CatalogueReads { get; private set; }
        public int Quotes { get; private set; }
        public int RefundRequests { get; private set; }
        public int ProfileReads { get; private set; }
        public bool PaymentFailure { get; set; }
        public bool RefundFailure { get; set; }
        public string? CheckoutError { get; set; }
        public (long, string, string, string, int)? Payment { get; private set; }
        public (long, string)? OrderLookup { get; private set; }
        public (long, string)? RecordedRefund { get; private set; }
        public PaymentResult PaymentResult { get; set; } = new(true, false, false);
        public Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct)
        { CatalogueReads++; return Task.FromResult(Packages); }
        public Task<Order> QuoteAsync(long userId, string packageCode, string requestKey, CancellationToken ct)
        {
            Quotes++;
            return Task.FromResult(new Order("order-1", userId, packageCode, "Turkey 1 GB", 123450, 200,
                "quoted", null, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), 0, null));
        }
        public Task<string?> ValidateCheckoutAsync(long userId, string orderId, string currency, int stars, CancellationToken ct)
            => Task.FromResult(CheckoutError);
        public Task<PaymentResult> RecordPaymentAsync(long userId, string orderId, string chargeId, string currency, int stars, CancellationToken ct)
        {
            if (PaymentFailure) throw new InvalidOperationException("Storage unavailable");
            Payment = (userId, orderId, chargeId, currency, stars);
            return Task.FromResult(PaymentResult);
        }
        public Task<IReadOnlyList<Order>> GetOrdersAsync(long userId, int page, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Order>>([]);
        public Task<Order?> GetOrderAsync(long userId, string orderId, CancellationToken ct)
        { OrderLookup = (userId, orderId); return Task.FromResult<Order?>(null); }
        public Task<StoredProfile?> GetProfileAsync(long userId, string orderId, CancellationToken ct)
        { ProfileReads++; return Task.FromResult<StoredProfile?>(null); }
        public Task<EsimUsage?> GetUsageAsync(long userId, string orderId, CancellationToken ct)
            => Task.FromResult<EsimUsage?>(null);
        public Task<bool> RequestRefundAsync(long adminUserId, string orderId, CancellationToken ct)
        { RefundRequests++; return Task.FromResult(true); }
        public Task<bool> RequestRetryAsync(long adminUserId, string orderId, CancellationToken ct)
            => Task.FromResult(true);
        public Task RecordRefundAsync(long userId, string chargeId, CancellationToken ct)
        {
            if (RefundFailure) throw new InvalidOperationException("Storage unavailable");
            RecordedRefund = (userId, chargeId); return Task.CompletedTask;
        }
    }

    private sealed class FakeLanguages : ILanguageService
    {
        public ConcurrentDictionary<long, string> Preferences { get; } = new();
        public bool FailReads { get; set; }
        public async Task<BotText> GetAsync(long userId, string? telegramLanguageCode, CancellationToken ct)
        {
            await Task.Yield();
            if (FailReads) throw new InvalidOperationException("Language store unavailable");
            var detected = telegramLanguageCode?.Split('-')[0] ?? "ru";
            return new BotText(Preferences.GetValueOrDefault(userId, detected));
        }
        public Task SetAsync(long userId, string language, CancellationToken ct)
        {
            Preferences[userId] = language;
            return Task.CompletedTask;
        }
    }
}

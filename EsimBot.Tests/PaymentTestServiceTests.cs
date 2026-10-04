using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;

namespace EsimBot.Tests;

public sealed class PaymentTestServiceTests
{
    [Fact]
    public async Task TestIdIsAvailableBeforeAdministratorIsConfigured()
    {
        using var f = new Fixture(admin: 0);
        await f.Service.HandleAsync(Message("/id"), default);
        Assert.Contains("42", f.Http.Requests.Single().Body.GetProperty("text").GetString());
        Assert.Equal(0, f.Store.Quotes);
    }

    [Fact]
    public async Task ProductionEnvironmentCannotUseTestService()
    {
        using var f = new Fixture(testEnvironment: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.HandleAsync(Message("/testpayment"), default));
        Assert.Empty(f.Http.Requests);
        Assert.Equal(0, f.Store.Quotes);
    }

    [Fact]
    public async Task OnlyPrivateConfiguredAdministratorCanCreateAnInvoice()
    {
        using var f = new Fixture();
        await f.Service.HandleAsync(Message("/testpayment", userId: 43), default);
        Assert.Equal(0, f.Store.Quotes);
        Assert.DoesNotContain(f.Http.Requests, request => request.Method == "sendInvoice");
        var group = Message("/testpayment");
        group.Message!.Chat.Type = ChatType.Group;
        group.Message.Chat.Id = -100;
        await f.Service.HandleAsync(group, default);
        Assert.Equal(0, f.Store.Quotes);
    }

    [Fact]
    public async Task InvoiceIsExplicitTestOnlyAndUsesSingleStarsPrice()
    {
        using var f = new Fixture();
        await f.Service.HandleAsync(Message("/testpayment"), default);
        var invoice = f.Http.Requests.Single(request => request.Method == "sendInvoice").Body;
        Assert.Contains("TEST", invoice.GetProperty("title").GetString());
        Assert.Contains("No eSIM", invoice.GetProperty("description").GetString());
        Assert.Equal("XTR", invoice.GetProperty("currency").GetString());
        Assert.Equal(1, invoice.GetProperty("prices").GetArrayLength());
        Assert.Equal(1, invoice.GetProperty("prices")[0].GetProperty("amount").GetInt32());
        Assert.Equal("test_order-1", invoice.GetProperty("start_parameter").GetString());
        Assert.True(invoice.GetProperty("protect_content").GetBoolean());
        Assert.Equal(PaymentTestOptions.PackageCode, f.Store.QuotedPackage!.Code);
        Assert.Equal(1, f.Store.QuotedPackage.PriceUnits);
        Assert.Equal("payment-test-v1", f.Store.Terms);
        Assert.Equal("test:update:17", f.Store.RequestKey);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("foreign-user", false)]
    [InlineData("wrong-amount", false)]
    [InlineData("wrong-currency", false)]
    [InlineData("expired", false)]
    [InlineData("non-test-package", false)]
    [InlineData("already-paid", false)]
    public async Task CheckoutChecksAdministratorPackageAmountCurrencyAndQuote(string scenario, bool allowed)
    {
        using var f = new Fixture();
        var query = new PreCheckoutQuery
        {
            Id = "query-1", From = Buyer(), Currency = "XTR", TotalAmount = 1, InvoicePayload = "order-1"
        };
        switch (scenario)
        {
            case "foreign-user": query.From = Buyer(43); break;
            case "wrong-amount": query.TotalAmount = 2; break;
            case "wrong-currency": query.Currency = "USD"; break;
            case "expired": f.Store.Order = f.Store.Order with { ExpiresAt = DateTime.UtcNow.AddMinutes(-1) }; break;
            case "non-test-package": f.Store.Order = f.Store.Order with { PackageCode = "real-esim" }; break;
            case "already-paid": f.Store.Order = f.Store.Order with { State = "paid" }; break;
        }
        await f.Service.HandleAsync(new Update { PreCheckoutQuery = query }, default);
        Assert.Equal(allowed, f.Http.Requests.Single().Body.GetProperty("ok").GetBoolean());
        Assert.Empty(f.Store.UniquePayments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedPaymentRemainsPaidWithoutEsimEvenWhenReceiptFails(bool languageFailure)
    {
        using var f = new Fixture();
        f.Languages.FailReads = languageFailure;
        f.Http.BlockMessages = !languageFailure;
        await f.Service.HandleAsync(Payment(), default);
        Assert.Equal("paid", f.Store.Order.State);
        Assert.Single(f.Store.UniquePayments);
        Assert.Equal(0, f.Store.RefundRequests);
    }

    [Fact]
    public async Task PaymentStorageFailurePreventsAcknowledgementAndNotification()
    {
        using var f = new Fixture();
        f.Store.FailPayment = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.HandleAsync(Payment(), default));
        Assert.Equal(0, f.Languages.Reads);
        Assert.Empty(f.Http.Requests);
    }

    [Fact]
    public async Task DuplicatePaymentDoesNotCreateAnotherPaymentOrEsim()
    {
        using var f = new Fixture();
        await f.Service.HandleAsync(Payment(), default);
        await f.Service.HandleAsync(Payment(), default);
        Assert.Single(f.Store.UniquePayments);
        Assert.Equal("paid", f.Store.Order.State);
        Assert.Equal(0, f.Store.Quotes);
        Assert.Contains(f.Http.Requests, request => request.Method == "sendMessage"
            && request.Body.GetProperty("text").GetString()!.Contains("already recorded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownPayerPaymentIsRecordedForRefund()
    {
        using var f = new Fixture();
        var payment = Payment();
        payment.Message!.From = Buyer(43);
        payment.Message.Chat.Id = 43;
        await f.Service.HandleAsync(payment, default);
        Assert.Single(f.Store.UniquePayments);
        Assert.True(f.Store.LastResult!.RefundQueued);
        Assert.Equal("quoted", f.Store.Order.State);
    }

    [Fact]
    public async Task AcceptedNonTestPackageIsQueuedForRefundNotFulfilled()
    {
        using var f = new Fixture();
        f.Store.Order = f.Store.Order with { PackageCode = "real-esim" };
        await f.Service.HandleAsync(Payment(), default);
        Assert.Equal("refund_pending", f.Store.Order.State);
        Assert.Equal(1, f.Store.RefundRequests);
    }

    [Fact]
    public async Task RefundButtonOnlyQueuesOwnedPaidTestOrder()
    {
        using var f = new Fixture();
        f.Store.Order = f.Store.Order with { State = "paid" };
        await f.Service.HandleAsync(Callback("testrefund:order-1"), default);
        Assert.Equal("refund_pending", f.Store.Order.State);
        Assert.Equal(1, f.Store.RefundRequests);
    }

    [Fact]
    public async Task RefundCommandDoesNotRefundNonTestPackage()
    {
        using var f = new Fixture();
        f.Store.Order = f.Store.Order with { State = "paid", PackageCode = "real-esim" };
        await f.Service.HandleAsync(Message("/refund order-1"), default);
        Assert.Equal(0, f.Store.RefundRequests);
    }

    [Fact]
    public async Task RefundServiceMessageUsesPrivateOwnerAndSurvivesReceiptFailure()
    {
        using var f = new Fixture();
        f.Http.BlockMessages = true;
        var update = Message("");
        update.Message!.From = new User { Id = 123456, IsBot = true, FirstName = "Test bot" };
        update.Message.RefundedPayment = new RefundedPayment
        { Currency = "XTR", TotalAmount = 1, InvoicePayload = "order-1", TelegramPaymentChargeId = "charge-1" };
        await f.Service.HandleAsync(update, default);
        Assert.Equal((42L, "charge-1"), f.Store.ExternalRefund);
        Assert.Equal("refunded", f.Store.Order.State);
    }

    [Theory]
    [InlineData("az")]
    [InlineData("ru")]
    public async Task TestLanguageSelectionPersists(string language)
    {
        using var f = new Fixture();
        await f.Service.HandleAsync(Callback("testlang:" + language), default);
        Assert.Equal(language, f.Languages.Selected);
        var message = f.Http.Requests.Single(request => request.Method == "sendMessage").Body.GetProperty("text").GetString();
        Assert.Equal(new BotText(language).Text("testing.language_saved"), message);
    }

    [Fact]
    public void TestServiceCannotCallSupplierOrShopThroughDependencies()
    {
        var types = typeof(PaymentTestService).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType);
        Assert.DoesNotContain(typeof(IEsimAccessClient), types);
        Assert.DoesNotContain(typeof(IShopService), types);
    }

    [Fact]
    public void TestingResourcesHaveMatchingKeysPlaceholdersAndTelegramLimits()
    {
        var assembly = typeof(BotText).Assembly;
        Dictionary<string, string>? baseline = null;
        foreach (var code in BotText.SupportedCodes)
        {
            var name = assembly.GetManifestResourceNames().Single(name => name.EndsWith($".Localization.Testing.{code}.json", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            baseline ??= entries;
            Assert.Equal(baseline.Keys.Order(), entries.Keys.Order());
            foreach (var (key, text) in entries)
            {
                var count = CompositeFormat.Parse(text).MinimumArgumentCount;
                Assert.Equal(CompositeFormat.Parse(baseline[key]).MinimumArgumentCount, count);
                var formatted = new BotText(code).Text(key, Enumerable.Repeat<object>("12345678901234567890123456789012", count).ToArray());
                Assert.InRange(formatted.Length, 1, key == "testing.invoice_title" ? 32 : key == "testing.invoice_description" ? 255 : 4096);
                if (key.StartsWith("testing.checkout_", StringComparison.Ordinal)) Assert.InRange(formatted.Length, 1, 200);
            }
        }
    }

    private static User Buyer(long id = 42) => new() { Id = id, FirstName = "Tester", LanguageCode = "en" };
    private static Update Message(string text, long userId = 42) => new()
    {
        Id = 17, Message = new Message { Id = 1, Chat = new Chat { Id = userId, Type = ChatType.Private }, From = Buyer(userId), Text = text }
    };
    private static Update Callback(string data) => new()
    {
        Id = 18, CallbackQuery = new CallbackQuery { Id = "callback-1", From = Buyer(), Message = Message("").Message!, Data = data }
    };
    private static Update Payment()
    {
        var update = Message("");
        update.Message!.SuccessfulPayment = new SuccessfulPayment
        { Currency = "XTR", TotalAmount = 1, InvoicePayload = "order-1", TelegramPaymentChargeId = "charge-1", ProviderPaymentChargeId = "" };
        return update;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        public TelegramRequests Http { get; } = new();
        public TestLanguages Languages { get; } = new();
        public OrderProxy Store { get; }
        public PaymentTestService Service { get; }
        public Fixture(long admin = 42, bool testEnvironment = true)
        {
            _http = new HttpClient(Http);
            var orders = DispatchProxy.Create<IOrderService, OrderProxy>();
            Store = (OrderProxy)orders;
            Service = new PaymentTestService(new TelegramBotClient("123456:TEST_TOKEN_NOT_A_REAL_SECRET", _http),
                orders, Languages, Options.Create(new PaymentTestOptions
                { Enabled = true, BotToken = "123456:TEST_TOKEN_NOT_A_REAL_SECRET", AdminUserId = admin, InvoiceStars = 1 }),
                Options.Create(new BotOptions { TestEnvironment = testEnvironment, DefaultLanguage = "en" }),
                NullLogger<PaymentTestService>.Instance);
        }
        public void Dispose() => _http.Dispose();
    }

    public class OrderProxy : DispatchProxy
    {
        public Order Order { get; set; } = new("order-1", 42, PaymentTestOptions.PackageCode, "TEST PAYMENT — NO eSIM",
            1, 1, "quoted", null, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15), 0, null);
        public Dictionary<string, PaymentResult> UniquePayments { get; } = new();
        public int Quotes { get; private set; }
        public int RefundRequests { get; private set; }
        public bool FailPayment { get; set; }
        public EsimPackage? QuotedPackage { get; private set; }
        public string? Terms { get; private set; }
        public string? RequestKey { get; private set; }
        public PaymentResult? LastResult { get; private set; }
        public (long User, string Charge)? ExternalRefund { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case nameof(IOrderService.CreateQuoteAsync):
                    Quotes++;
                    QuotedPackage = (EsimPackage)args![1]!;
                    Terms = (string)args[3]!;
                    RequestKey = (string)args[4]!;
                    return Task.FromResult(Order);
                case nameof(IOrderService.GetAsync):
                    return Task.FromResult<Order?>((string)args![0]! == Order.Id
                        && (args[1] is null || (long)args[1]! == Order.UserId) ? Order : null);
                case nameof(IOrderService.ListAsync): return Task.FromResult<IReadOnlyList<Order>>([Order]);
                case nameof(IOrderService.RecordPaymentAsync):
                    if (FailPayment) throw new InvalidOperationException("Persistence unavailable");
                    var charge = (string)args![2]!;
                    if (UniquePayments.TryGetValue(charge, out var existing)) return Task.FromResult(existing with { Duplicate = true });
                    var accepted = (long)args[0]! == Order.UserId && (string)args[1]! == Order.Id
                        && (int)args[3]! == Order.Stars && Order.State == "quoted";
                    LastResult = new PaymentResult(accepted, false, !accepted);
                    UniquePayments[charge] = LastResult;
                    if (accepted) Order = Order with { State = "paid" };
                    return Task.FromResult(LastResult);
                case nameof(IOrderService.QueueUnsubmittedRefundAsync):
                    RefundRequests++;
                    var queued = (string)args![0]! == Order.Id && Order.State == "paid";
                    if (queued) Order = Order with { State = "refund_pending" };
                    return Task.FromResult(queued);
                case nameof(IOrderService.RecordExternalRefundAsync):
                    ExternalRefund = ((long)args![0]!, (string)args[1]!);
                    Order = Order with { State = "refunded" };
                    return Task.FromResult<string?>(null);
                default: throw new InvalidOperationException("Test payments must never request eSIM allocation: " + method?.Name);
            }
        }
    }

    private sealed class TestLanguages : ILanguageService
    {
        public bool FailReads { get; set; }
        public int Reads { get; private set; }
        public string Selected { get; private set; } = "en";
        public Task<BotText> GetAsync(long userId, string? telegramLanguageCode, CancellationToken ct)
        {
            Reads++;
            if (FailReads) throw new InvalidOperationException("Locale store unavailable");
            return Task.FromResult(new BotText(Selected));
        }
        public Task SetAsync(long userId, string language, CancellationToken ct) { Selected = language; return Task.CompletedTask; }
    }

    private sealed class TelegramRequests : HttpMessageHandler
    {
        public List<(string Method, JsonElement Body)> Requests { get; } = [];
        public bool BlockMessages { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.Segments.Last();
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add((method, document.RootElement.Clone()));
            if (BlockMessages && method == "sendMessage")
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                { Content = new StringContent("{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden: bot blocked\"}", Encoding.UTF8, "application/json") };
            var result = method.StartsWith("answer", StringComparison.Ordinal) ? "true"
                : "{\"message_id\":1,\"date\":0,\"chat\":{\"id\":42,\"type\":\"private\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
        }
    }
}

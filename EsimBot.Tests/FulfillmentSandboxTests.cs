using System.Net;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace EsimBot.Tests;

public sealed class FulfillmentSandboxTests
{
    [Fact]
    public async Task PaymentTestingDoesNotEvenReadTheFulfillmentQueue()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = Store((method, _) =>
        {
            if (method == nameof(IOrderService.DueRefundsAsync))
            {
                stop.Cancel();
                return Task.FromResult<IReadOnlyList<Payment>>([]);
            }
            return Unexpected(method, stop);
        });
        var provider = new ProviderSpy();
        var notifier = new NotifierSpy();
        using var httpHandler = new TelegramHandler(false);
        using var http = new HttpClient(httpHandler);
        var fulfillment = Service(store, provider, notifier, http, true);

        await RunUntilStoppedAsync(fulfillment, stop.Token);

        Assert.Equal([nameof(IOrderService.DueRefundsAsync)], ((StoreProxy)store).Calls);
        Assert.Empty(provider.Calls);
        Assert.Equal(0, notifier.Deliveries);
        Assert.Equal(0, notifier.Alerts);
        Assert.Empty(httpHandler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaymentTestingCompletesRefundThroughSelectedTestClientWithoutFulfillment(bool alreadyRefunded)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var payment = new Payment("test-charge", "test-order", 42, 1, "refund_pending", DateTime.UtcNow);
        var completed = false;
        var store = Store((method, args) => method switch
        {
            nameof(IOrderService.DueRefundsAsync) => Task.FromResult<IReadOnlyList<Payment>>([payment]),
            nameof(IOrderService.CompleteRefundAsync) => Complete(),
            nameof(IOrderService.GetAsync) => Task.FromResult<Order?>(Order("refunded")),
            _ => Unexpected(method, stop)
        });
        Task Complete()
        {
            completed = true;
            return Task.CompletedTask;
        }
        var provider = new ProviderSpy();
        var notifier = new NotifierSpy { OnRefunded = () => stop.Cancel() };
        using var httpHandler = new TelegramHandler(alreadyRefunded);
        using var http = new HttpClient(httpHandler);

        await RunUntilStoppedAsync(Service(store, provider, notifier, http, true), stop.Token);

        Assert.True(completed);
        Assert.Equal(1, notifier.RefundReceipts);
        Assert.Equal(0, notifier.Deliveries);
        Assert.Equal(0, notifier.Alerts);
        Assert.DoesNotContain(nameof(IOrderService.DueOrdersAsync), ((StoreProxy)store).Calls);
        Assert.DoesNotContain(nameof(IOrderService.RetryRefundAsync), ((StoreProxy)store).Calls);
        Assert.Empty(provider.Calls);
        var request = Assert.Single(httpHandler.Requests);
        Assert.EndsWith("/test/refundStarPayment", request.Url, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal(42, json.RootElement.GetProperty("user_id").GetInt64());
        Assert.Equal("test-charge", json.RootElement.GetProperty("telegram_payment_charge_id").GetString());
    }

    [Theory]
    [InlineData("paid", 0, 1)]
    [InlineData("provisioning", 0, 1)]
    [InlineData("ready", 0, 1)]
    [InlineData("ready", 1, 0)]
    public async Task SwitchingToProductionCannotPurchaseOrDeliverCopiedTestOrder(string state, int attempts, int alerts)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (string Code, int Delay, bool Manual)? retry = null;
        var store = Store((method, args) => method switch
        {
            nameof(IOrderService.DueRefundsAsync) => Task.FromResult<IReadOnlyList<Payment>>([]),
            nameof(IOrderService.DueOrdersAsync) => Task.FromResult<IReadOnlyList<Order>>([Order(state) with { Attempts = attempts }]),
            nameof(IOrderService.RetryOrderAsync) => Defer(args),
            _ => Unexpected(method, stop)
        });
        Task Defer(object?[] args)
        {
            Assert.Equal("test-order", args[0]);
            retry = ((string)args[1]!, (int)args[2]!, (bool)args[3]!);
            stop.Cancel();
            return Task.CompletedTask;
        }
        var provider = new ProviderSpy();
        var notifier = new NotifierSpy();
        using var httpHandler = new TelegramHandler(false);
        using var http = new HttpClient(httpHandler);

        await RunUntilStoppedAsync(Service(store, provider, notifier, http, false), stop.Token);

        Assert.Equal(("reserved_test_package", 86400, true), retry);
        Assert.Empty(provider.Calls);
        Assert.DoesNotContain(nameof(IOrderService.GetProfileAsync), ((StoreProxy)store).Calls);
        Assert.DoesNotContain(nameof(IOrderService.MarkDeliveredAsync), ((StoreProxy)store).Calls);
        Assert.Equal(0, notifier.Deliveries);
        Assert.Equal(alerts, notifier.Alerts);
        if (alerts > 0) Assert.Equal("reserved_test_package", notifier.LastAlertCode);
        Assert.Empty(httpHandler.Requests);
    }

    [Fact]
    public async Task NormalProductionOrderStillUsesTheProviderAndSavesItsProfile()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var saved = false;
        var store = Store((method, args) => method switch
        {
            nameof(IOrderService.DueRefundsAsync) => Task.FromResult<IReadOnlyList<Payment>>([]),
            nameof(IOrderService.DueOrdersAsync) => Task.FromResult<IReadOnlyList<Order>>([Order("paid") with { PackageCode = "TR_1_7" }]),
            nameof(IOrderService.BeginProvisionAsync) => Task.FromResult(true),
            nameof(IOrderService.SaveProviderOrderAsync) => Task.CompletedTask,
            nameof(IOrderService.SaveProfileAsync) => SaveProfile(args),
            _ => Unexpected(method, stop)
        });
        Task SaveProfile(object?[] args)
        {
            Assert.IsType<ProviderProfile>(args[1]);
            saved = true;
            stop.Cancel();
            return Task.CompletedTask;
        }
        var provider = new ProviderSpy();
        using var httpHandler = new TelegramHandler(false);
        using var http = new HttpClient(httpHandler);

        await RunUntilStoppedAsync(Service(store, provider, new NotifierSpy(), http, false), stop.Token);

        Assert.True(saved);
        Assert.Equal(["IsConfigured", "PlaceOrderAsync", "QueryOrderAsync"], provider.Calls);
        Assert.Empty(httpHandler.Requests);
    }

    private static Fulfillment Service(IOrderService store, ProviderSpy provider, NotifierSpy notifier,
        HttpClient http, bool testing) => new(store, provider, notifier,
        new TelegramBotClient(new TelegramBotClientOptions("123456:FAKE_SANDBOX_TOKEN_FOR_UNIT_TEST", useTestEnvironment: testing), http),
        NullLogger<Fulfillment>.Instance, Options.Create(new PaymentTestOptions { Enabled = testing }));

    private static async Task RunUntilStoppedAsync(Fulfillment fulfillment, CancellationToken ct)
    {
        try { await fulfillment.RunAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static Order Order(string state) => new("test-order", 42, PaymentTestOptions.PackageCode,
        "Payment test", 1, 1, state, null, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15), 0, null);

    private static IOrderService Store(Func<string, object?[], object?> invoke)
    {
        var store = DispatchProxy.Create<IOrderService, StoreProxy>();
        ((StoreProxy)store).Callback = invoke;
        return store;
    }

    private static object Unexpected(string method, CancellationTokenSource stop)
    {
        stop.Cancel();
        throw new InvalidOperationException("Unexpected order operation: " + method);
    }

    public class StoreProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Callback { get; set; } = null!;
        public ConcurrentQueue<string> Calls { get; } = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Enqueue(method!.Name);
            return Callback(method.Name, args ?? []);
        }
    }

    private sealed class ProviderSpy : IEsimAccessClient
    {
        public List<string> Calls { get; } = [];
        public bool IsConfigured { get { Calls.Add(nameof(IsConfigured)); return true; } }
        public Task<string> PlaceOrderAsync(string transactionId, string packageCode, long priceUnits, CancellationToken ct)
        {
            Calls.Add(nameof(PlaceOrderAsync));
            return Task.FromResult("supplier-order");
        }
        public Task<ProviderOrder?> QueryOrderAsync(string transactionId, string? orderNumber, CancellationToken ct)
        {
            Calls.Add(nameof(QueryOrderAsync));
            return Task.FromResult<ProviderOrder?>(new("supplier-order",
                [new ProviderProfile("profile", "0012", "LPA:1$test$activation", null, "GOT_RESOURCE")]));
        }
        public Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct)
        { Calls.Add(nameof(GetPackagesAsync)); throw new NotSupportedException(); }
        public Task<long> GetBalanceUnitsAsync(CancellationToken ct)
        { Calls.Add(nameof(GetBalanceUnitsAsync)); throw new NotSupportedException(); }
        public Task<EsimUsage> GetUsageAsync(string esimId, CancellationToken ct)
        { Calls.Add(nameof(GetUsageAsync)); throw new NotSupportedException(); }
    }

    private sealed class NotifierSpy : IBotNotifier
    {
        public int Deliveries { get; private set; }
        public int RefundReceipts { get; private set; }
        public int Alerts { get; private set; }
        public string? LastAlertCode { get; private set; }
        public Action? OnRefunded { get; init; }
        public Task DeliverAsync(Order order, StoredProfile profile, CancellationToken ct)
        { Deliveries++; return Task.CompletedTask; }
        public Task RefundedAsync(Order order, CancellationToken ct)
        { RefundReceipts++; OnRefunded?.Invoke(); return Task.CompletedTask; }
        public Task AlertAdminAsync(string orderId, string code, CancellationToken ct)
        { Alerts++; LastAlertCode = code; return Task.CompletedTask; }
    }

    private sealed class TelegramHandler(bool alreadyRefunded) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(alreadyRefunded ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(alreadyRefunded
                    ? """{"ok":false,"error_code":400,"description":"Bad Request: CHARGE_ALREADY_REFUNDED"}"""
                    : """{"ok":true,"result":true}""", Encoding.UTF8, "application/json")
            };
        }
    }
}

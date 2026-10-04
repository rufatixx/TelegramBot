using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace EsimBot.Tests;

public sealed class FulfillmentCapacityTests
{
    [Fact]
    public async Task RefundsAndIssuanceProgressIndependentlyAndIssuanceHasThreeSlots()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var threePurchasesEntered = Signal();
        var refundCompleted = Signal();
        var ordersRead = 0;
        var entered = new ConcurrentQueue<string>();
        var store = Proxy((method, _) => method switch
        {
            nameof(IOrderService.DueRefundsAsync) => Refunds(),
            nameof(IOrderService.DueOrdersAsync) => Orders(),
            nameof(IOrderService.BeginProvisionAsync) => Task.FromResult(true),
            nameof(IOrderService.CompleteRefundAsync) => Complete(),
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });
        async Task<IReadOnlyList<Payment>> Refunds()
        {
            // A slow refund queue read must not prevent the supplier queue from starting.
            await threePurchasesEntered.Task.WaitAsync(stop.Token);
            return [new Payment("fake-charge", null, 1, 1, "refund_pending", DateTime.UtcNow)];
        }
        Task<IReadOnlyList<Order>> Orders()
        {
            Interlocked.Increment(ref ordersRead);
            return Task.FromResult<IReadOnlyList<Order>>(Enumerable.Range(1, 10).Select(id => Order(id.ToString(), "paid")).ToArray());
        }
        Task Complete() { refundCompleted.TrySetResult(); return Task.CompletedTask; }
        var provider = new Provider
        {
            Place = async (id, ct) =>
            {
                entered.Enqueue(id);
                if (entered.Count == 3) threePurchasesEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            }
        };
        using var http = new HttpClient(new TelegramStub());
        var service = Service(store, provider, http);
        var running = service.RunAsync(stop.Token);
        try
        {
            // Slow purchases must not prevent the refund from completing.
            await refundCompleted.Task.WaitAsync(stop.Token);
            Assert.Equal(3, entered.Count);
            Assert.Equal(3, entered.Distinct().Count());
            Assert.Equal(1, Volatile.Read(ref ordersRead));
        }
        finally { stop.Cancel(); await Stopped(running, stop.Token); }
    }

    [Fact]
    public async Task KnownShortageDefersPaidOrderAndReportsExactAmountsBeforeProvisioning()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var order = Order("waiting-for-topup", "paid") with { CostUnits = 25_000, Attempts = 50 };
        var calls = new ConcurrentQueue<string>();
        (string Code, int Delay, bool Manual)? retry = null;
        var store = Proxy((method, args) =>
        {
            calls.Enqueue(method);
            return method switch
            {
                nameof(IOrderService.DueRefundsAsync) => Task.FromResult<IReadOnlyList<Payment>>([]),
                nameof(IOrderService.DueOrdersAsync) => Task.FromResult<IReadOnlyList<Order>>([order]),
                nameof(IOrderService.RetryOrderAsync) => Retry(args),
                _ => throw new InvalidOperationException("Unexpected store operation: " + method)
            };
        });
        Task Retry(object?[] args)
        {
            retry = ((string)args[1]!, (int)args[2]!, (bool)args[3]!);
            stop.Cancel();
            return Task.CompletedTask;
        }
        var provider = new Provider { Balance = 10_000 };
        var monitor = new Monitor();
        using var http = new HttpClient(new TelegramStub());
        await Stopped(Service(store, provider, http, monitor).RunAsync(stop.Token), stop.Token);

        Assert.Equal(("supplier_balance_low", 30, false), retry);
        Assert.Equal((25_000L, 10_000L), Assert.Single(monitor.Notices));
        Assert.Equal(1, provider.BalanceReads);
        Assert.Equal(0, provider.Purchases);
        Assert.DoesNotContain(nameof(IOrderService.BeginProvisionAsync), calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("supplier-existing")]
    public async Task AmbiguousProvisioningContinuesReconciliationEvenWhenBalanceIsZero(string? supplierOrder)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var order = Order("immutable-transaction", "provisioning") with { ProviderOrderNumber = supplierOrder };
        var queryTransaction = "";
        var purchaseTransaction = "";
        var saved = false;
        var store = Proxy((method, _) => method switch
        {
            nameof(IOrderService.DueRefundsAsync) => Task.FromResult<IReadOnlyList<Payment>>([]),
            nameof(IOrderService.DueOrdersAsync) => Task.FromResult<IReadOnlyList<Order>>([order]),
            nameof(IOrderService.SaveProviderOrderAsync) => Task.CompletedTask,
            nameof(IOrderService.SaveProfileAsync) => Save(),
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });
        Task Save() { saved = true; stop.Cancel(); return Task.CompletedTask; }
        var provider = new Provider
        {
            Balance = 0,
            Place = (id, _) => { purchaseTransaction = id; return Task.FromResult("supplier-existing"); },
            Query = (id, _, _) =>
            {
                queryTransaction = id;
                return Task.FromResult<ProviderOrder?>(new("supplier-existing", [Profile()]));
            }
        };
        var monitor = new Monitor();
        using var http = new HttpClient(new TelegramStub());
        await Stopped(Service(store, provider, http, monitor).RunAsync(stop.Token), stop.Token);

        Assert.True(saved);
        Assert.Equal(order.Id, queryTransaction);
        Assert.Equal(supplierOrder is null ? order.Id : "", purchaseTransaction);
        Assert.Equal(0, provider.BalanceReads);
        Assert.Empty(monitor.Notices);
    }

    [Fact]
    public async Task BatchNeverIssuesOneOrderTwiceEvenIfInputContainsDuplicateIds()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var saved = Signal();
        var one = Order("same-order", "provisioning");
        var provider = new Provider();
        var store = Proxy((method, _) => method switch
        {
            nameof(IOrderService.DueRefundsAsync) => Task.FromResult<IReadOnlyList<Payment>>([]),
            nameof(IOrderService.DueOrdersAsync) => Task.FromResult<IReadOnlyList<Order>>([one, one, one]),
            nameof(IOrderService.SaveProviderOrderAsync) => Task.CompletedTask,
            nameof(IOrderService.SaveProfileAsync) => Save(),
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });
        Task Save() { saved.TrySetResult(); return Task.CompletedTask; }
        using var http = new HttpClient(new TelegramStub());
        var running = Service(store, provider, http).RunAsync(stop.Token);
        try
        {
            await saved.Task.WaitAsync(stop.Token);
            Assert.Equal(1, provider.Purchases);
        }
        finally { stop.Cancel(); await Stopped(running, stop.Token); }
    }

    [Fact]
    public async Task SandboxGuardRunsBeforeBalanceCheckAndBeforeSupplierCalls()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reserved = Order("reserved", "paid") with { PackageCode = PaymentTestOptions.PackageCode, Attempts = 1 };
        var store = Proxy((method, _) => method switch
        {
            nameof(IOrderService.DueRefundsAsync) => Task.FromResult<IReadOnlyList<Payment>>([]),
            nameof(IOrderService.DueOrdersAsync) => Task.FromResult<IReadOnlyList<Order>>([reserved]),
            nameof(IOrderService.RetryOrderAsync) => Stop(),
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });
        Task Stop() { stop.Cancel(); return Task.CompletedTask; }
        var provider = new Provider();
        var monitor = new Monitor();
        using var http = new HttpClient(new TelegramStub());
        await Stopped(Service(store, provider, http, monitor).RunAsync(stop.Token), stop.Token);
        Assert.Equal(0, provider.BalanceReads);
        Assert.Equal(0, provider.Purchases);
        Assert.Empty(monitor.Notices);
    }

    private static Fulfillment Service(IOrderService store, Provider provider, HttpClient http, Monitor? monitor = null)
        => new(store, provider, new Notifier(), new TelegramBotClient("123456:FAKE_CAPACITY_TEST_TOKEN", http),
            NullLogger<Fulfillment>.Instance, Options.Create(new PaymentTestOptions()), monitor);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Stopped(Task running, CancellationToken ct)
    {
        try { await running; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private static Order Order(string id, string state) => new(id, 1, "TR_FIXED", "Travel data", 10_000, 100,
        state, null, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15), 0, null);
    private static ProviderProfile Profile() => new("profile", "fake-iccid", "LPA:1$test$activation", null, "GOT_RESOURCE");
    private static IOrderService Proxy(Func<string, object?[], object?> callback)
    {
        var store = DispatchProxy.Create<IOrderService, StoreProxy>();
        ((StoreProxy)store).Callback = callback;
        return store;
    }
    public class StoreProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Callback { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Callback(method!.Name, args ?? []);
    }
    private sealed class Provider : IEsimAccessClient
    {
        public bool IsConfigured => true;
        public long Balance { get; init; } = long.MaxValue;
        public int BalanceReads;
        public int Purchases;
        public Func<string, CancellationToken, Task<string>>? Place { get; init; }
        public Func<string, string?, CancellationToken, Task<ProviderOrder?>>? Query { get; init; }
        public Task<long> GetBalanceUnitsAsync(CancellationToken ct)
        { Interlocked.Increment(ref BalanceReads); return Task.FromResult(Balance); }
        public Task<string> PlaceOrderAsync(string transactionId, string packageCode, long priceUnits, CancellationToken ct)
        { Interlocked.Increment(ref Purchases); return Place?.Invoke(transactionId, ct) ?? Task.FromResult("supplier-existing"); }
        public Task<ProviderOrder?> QueryOrderAsync(string transactionId, string? orderNumber, CancellationToken ct)
            => Query?.Invoke(transactionId, orderNumber, ct) ?? Task.FromResult<ProviderOrder?>(new("supplier-existing", [Profile()]));
        public Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EsimUsage> GetUsageAsync(string esimId, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Monitor : IBalanceMonitorService
    {
        public ConcurrentQueue<(long Required, long Balance)> Notices { get; } = new();
        public void ReportInsufficient(long requiredUnits, long balanceUnits) => Notices.Enqueue((requiredUnits, balanceUnits));
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Notifier : IBotNotifier
    {
        public Task DeliverAsync(Order order, StoredProfile profile, CancellationToken ct) => Task.CompletedTask;
        public Task RefundedAsync(Order order, CancellationToken ct) => Task.CompletedTask;
        public Task AlertAdminAsync(string orderId, string code, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class TelegramStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"ok":true,"result":true}""", Encoding.UTF8, "application/json") });
    }
}

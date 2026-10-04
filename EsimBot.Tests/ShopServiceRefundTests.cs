using System.Reflection;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EsimBot.Tests;

public sealed class ShopServiceRefundTests
{
    [Fact]
    public async Task PersistedRefundIsHandledEvenWhenAdminCannotBeNotified()
    {
        var recorded = false;
        var store = RefundStore((user, charge, _) =>
        {
            Assert.Equal(42, user);
            Assert.Equal("charge-1", charge);
            recorded = true;
            return Task.FromResult<string?>("review-order");
        });
        var notifier = new UnavailableNotifier();
        using var http = new HttpClient();
        await Service(http, store, notifier).RecordRefundAsync(42, "charge-1", default);
        Assert.True(recorded);
        Assert.Equal(1, notifier.Attempts);
    }

    [Fact]
    public async Task RefundStorageFailureStillPropagatesAndDoesNotNotifyAdmin()
    {
        var error = new InvalidOperationException("Persistence failed");
        var store = RefundStore((_, _, _) => Task.FromException<string?>(error));
        var notifier = new UnavailableNotifier();
        using var http = new HttpClient();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(http, store, notifier).RecordRefundAsync(42, "charge-1", default));
        Assert.Same(error, thrown);
        Assert.Equal(0, notifier.Attempts);
    }

    private static ShopService Service(HttpClient http, IOrderService orders, IBotNotifier notifier) => new(
        new EsimAccessClient(http, Options.Create(new ProviderOptions()), Options.Create(new PaymentTestOptions())), orders,
        new Pricing(Options.Create(new SalesOptions())), Options.Create(new SalesOptions()),
        Options.Create(new BotOptions()), notifier, NullLogger<ShopService>.Instance);

    private static IOrderService RefundStore(Func<long, string, CancellationToken, Task<string?>> record)
    {
        var service = DispatchProxy.Create<IOrderService, RefundStoreProxy>();
        ((RefundStoreProxy)service).Record = record;
        return service;
    }

    // Keep this test double scoped to the refund boundary. Unexpected order operations fail.
    public class RefundStoreProxy : DispatchProxy
    {
        public Func<long, string, CancellationToken, Task<string?>> Record { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IOrderService.RecordExternalRefundAsync)
            ? Record((long)args![0]!, (string)args[1]!, (CancellationToken)args[2]!)
            : throw new InvalidOperationException("Unexpected order operation.");
    }

    private sealed class UnavailableNotifier : IBotNotifier
    {
        public int Attempts { get; private set; }
        public Task AlertAdminAsync(string orderId, string code, CancellationToken ct)
        {
            Attempts++;
            Assert.Equal("review-order", orderId);
            Assert.Equal("refund_after_supplier_submission", code);
            throw new InvalidOperationException("Admin has blocked the bot.");
        }
        public Task DeliverAsync(Order order, StoredProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task RefundedAsync(Order order, CancellationToken ct) => throw new NotSupportedException();
    }
}

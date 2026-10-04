using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;

namespace EsimBot.Tests;

public sealed class CatalogueConcurrencyTests
{
    [Fact]
    public async Task ThousandSimultaneousColdReadersShareOneUpstreamRefresh()
    {
        var release = Completion();
        var provider = new FakeProvider(_ => release.Task);
        var catalog = new CatalogService(provider);
        var readers = Enumerable.Range(0, 1000).Select(_ => catalog.GetPackagesAsync(default)).ToArray();
        Assert.Equal(1, provider.Calls);
        release.SetResult([Package()]);
        var results = await Task.WhenAll(readers);
        Assert.All(results, result => Assert.Same(results[0], result));
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => catalog.GetPackagesAsync(default)));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task OneCancelledReaderDoesNotCancelRefreshOrOtherReaders()
    {
        var release = Completion();
        CancellationToken refreshToken = default;
        var provider = new FakeProvider(ct => { refreshToken = ct; return release.Task; });
        var catalog = new CatalogService(provider);
        using var cancelled = new CancellationTokenSource();
        var first = catalog.GetPackagesAsync(cancelled.Token);
        var second = catalog.GetPackagesAsync(default);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(refreshToken.IsCancellationRequested);
        release.SetResult([Package()]);
        Assert.Single(await second);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ExpiredSnapshotHasOnlyOneRefreshAndQuotesCannotUseStaleDataOnFailure()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider(_ => Task.FromResult<IReadOnlyList<EsimPackage>>([Package()]));
        var catalog = new CatalogService(provider, clock);
        Assert.Single(await catalog.GetPackagesAsync(default));
        clock.Advance(TimeSpan.FromMinutes(2));
        var release = Completion();
        provider.Load = _ => release.Task;
        var readers = Enumerable.Range(0, 200).Select(_ => catalog.GetPackagesAsync(default)).ToArray();
        Assert.Equal(2, provider.Calls);
        release.SetException(new ProviderException("http_503", true));
        foreach (var reader in readers)
            Assert.Equal("http_503", (await Assert.ThrowsAsync<ProviderException>(() => reader)).Code);
        var afterFailure = await Assert.ThrowsAsync<ProviderException>(() => catalog.GetPackagesAsync(default));
        Assert.Equal("http_503", afterFailure.Code);
        Assert.Equal(2, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(5));
        provider.Load = _ => Task.FromResult<IReadOnlyList<EsimPackage>>([Package() with { PriceUnits = 20000 }]);
        Assert.Equal(20000, Assert.Single(await catalog.GetPackagesAsync(default)).PriceUnits);
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task ColdFailureHasBackoffInsteadOfOneSupplierRetryPerUser()
    {
        var provider = new FakeProvider(_ => throw new InvalidOperationException("Internal transport details"));
        var catalog = new CatalogService(provider);
        for (var i = 0; i < 200; i++)
        {
            var error = await Assert.ThrowsAsync<ProviderException>(() => catalog.GetPackagesAsync(default));
            Assert.Equal("catalog_unavailable", error.Code);
            Assert.DoesNotContain("transport", error.Message);
        }
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task PublishedSnapshotDoesNotExposeMutableProviderCollections()
    {
        var countries = new List<Country> { new("AZ", "Azerbaijan") };
        var packages = new List<EsimPackage> { Package() with { Countries = countries } };
        var catalog = new CatalogService(new FakeProvider(_ => Task.FromResult<IReadOnlyList<EsimPackage>>(packages)));
        var snapshot = await catalog.GetPackagesAsync(default);
        countries.Clear();
        packages.Clear();
        Assert.Equal("AZ", Assert.Single(Assert.Single(snapshot).Countries).Code);
        Assert.Throws<NotSupportedException>(() => ((IList<EsimPackage>)snapshot).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Country>)snapshot[0].Countries).Clear());
    }

    [Fact]
    public async Task PreCancelledCallerDoesNotStartUpstreamWork()
    {
        var provider = new FakeProvider(_ => throw new InvalidOperationException());
        var catalog = new CatalogService(provider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetPackagesAsync(new CancellationToken(true)));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task SlowRefreshDoesNotHoldCallerIndefinitelyAndRemainsShared()
    {
        var release = Completion();
        var provider = new FakeProvider(_ => release.Task);
        var catalog = new CatalogService(provider);
        var error = await Assert.ThrowsAsync<ProviderException>(() => catalog.GetPackagesAsync(default).WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.Equal("catalog_busy", error.Code);
        var second = catalog.GetPackagesAsync(default);
        Assert.Equal(1, provider.Calls);
        release.SetResult([Package()]);
        Assert.Single(await second);
    }

    private static TaskCompletionSource<IReadOnlyList<EsimPackage>> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static EsimPackage Package() => new("AZ1", "Azerbaijan 1GB", 10000, "USD", 1073741824,
        7, "DAY", [new Country("AZ", "Azerbaijan")], "", "first_connection", "4G", "1");

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeProvider(Func<CancellationToken, Task<IReadOnlyList<EsimPackage>>> load) : IEsimAccessClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Func<CancellationToken, Task<IReadOnlyList<EsimPackage>>> Load { get; set; } = load;
        public bool IsConfigured => true;
        public Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Load(ct);
        }
        public Task<long> GetBalanceUnitsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> PlaceOrderAsync(string transactionId, string packageCode, long priceUnits, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderOrder?> QueryOrderAsync(string transactionId, string? orderNumber, CancellationToken ct) => throw new NotSupportedException();
        public Task<EsimUsage> GetUsageAsync(string esimId, CancellationToken ct) => throw new NotSupportedException();
    }
}

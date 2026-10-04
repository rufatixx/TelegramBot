using System.Reflection;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EsimBot.Tests;

public sealed class SalesDefaultsTests
{
    [Fact]
    public void BuiltInSalesRequireNoMerchantOverrides()
    {
        var sales = new SalesOptions();
        Assert.True(sales.IsConfigured);
        Assert.Equal(0.0117m, sales.NetUsdPerStar);
        Assert.Equal(10m, sales.MarkupPercent);
        Assert.Equal(0.25m, sales.MinimumProfitUsd);
        Assert.Equal(SalesOptions.BuiltInTermsVersion, sales.TermsVersion);
        Assert.Empty(sales.TermsUrl);
        Assert.Empty(sales.SupportUrl);
        Assert.Equal(107, new Pricing(Options.Create(sales)).StarsFor(Package()));
    }

    [Fact]
    public void OnlyCredentialsAndAdministratorAreNeededForStartupConfiguration()
    {
        var ready = StartupConfiguration.Inspect(Config());
        Assert.True(ready.CanStartWorker);
        Assert.True(ready.SalesConfigured);
        Assert.True(ready.CanSell);
        Assert.False(ready.TestMode);
        Assert.Equal("Polling", BotOptions.Resolve(Config()).Mode);
    }

    [Theory]
    [InlineData("TelegramBot:AdminUserId", "0")]
    [InlineData("TelegramBot:Token", "")]
    [InlineData("Provider:AccessCode", "")]
    [InlineData("Storage:EncryptionKey", "")]
    [InlineData("ConnectionStrings:EsimBot", "")]
    [InlineData("Sales:Enabled", "false")]
    [InlineData("Sales:NetUsdPerStar", "0")]
    [InlineData("Sales:NetUsdPerStar", "1.1")]
    [InlineData("Sales:TermsVersion", "")]
    [InlineData("Sales:MarkupPercent", "-1")]
    [InlineData("Sales:MinimumProfitUsd", "-0.01")]
    [InlineData("Sales:SupportUrl", "http://example.test")]
    [InlineData("Sales:SupportUrl", "https://user:password@example.test")]
    [InlineData("Sales:TermsUrl", "https://example.test/terms")]
    public void ExplicitInvalidOrPausedConfigurationIsNotOverridden(string key, string value)
        => Assert.False(StartupConfiguration.Inspect(Config(key, value)).CanSell);

    [Fact]
    public void CustomTermsRequireTheirOwnBoundedVersionAndHttps()
    {
        Assert.True(new SalesOptions { TermsUrl = "https://example.test/terms", TermsVersion = "merchant-v1" }.IsConfigured);
        Assert.False(new SalesOptions { TermsUrl = "http://example.test/terms", TermsVersion = "merchant-v1" }.IsConfigured);
        Assert.False(new SalesOptions { TermsVersion = new string('a', 65) }.IsConfigured);
        Assert.True(new SalesOptions { TermsVersion = new string('a', 64) }.IsConfigured);
    }

    [Fact]
    public void TopicsFeeIsAutomaticallyIncludedAndOldUnderpricedInvoiceIsRejected()
    {
        var revenue = new Revenue();
        var pricing = new Pricing(Options.Create(new SalesOptions()), revenue);
        var previousStars = pricing.StarsFor(Package());
        Assert.True(pricing.CoversQuote(10000, previousStars));
        revenue.Retention = 0.85m;
        Assert.Equal(126, pricing.StarsFor(Package()));
        Assert.False(pricing.CoversQuote(10000, previousStars));
        Assert.True(pricing.CoversQuote(10000, 126));
    }

    [Theory]
    [InlineData(0, "ui.checkout_no_balance")]
    [InlineData(9999, "ui.checkout_no_balance")]
    [InlineData(10000, null)]
    [InlineData(10001, null)]
    public async Task RealShopChecksWalletBeforeApprovingCheckout(long balance, string? expected)
    {
        var fixture = new Fixture { Balance = balance };
        Assert.Equal(expected, await fixture.Shop().ValidateCheckoutAsync(42, "order", "XTR", 107, default));
        Assert.Equal(1, fixture.BalanceReads);
    }

    [Fact]
    public async Task WalletFailurePropagatesSoHandlerCanRejectPayment()
    {
        var fixture = new Fixture { BalanceFails = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Shop().ValidateCheckoutAsync(42, "order", "XTR", 107, default));
    }

    [Fact]
    public async Task FeeChangeRejectsOldInvoiceWithoutReadingWallet()
    {
        var fixture = new Fixture();
        fixture.Revenue.Retention = 0.85m;
        Assert.Equal("ui.checkout_price_changed", await fixture.Shop().ValidateCheckoutAsync(42, "order", "XTR", 107, default));
        Assert.Equal(0, fixture.BalanceReads);
    }

    [Theory]
    [InlineData(false, "ui.checkout_price_changed")]
    [InlineData(true, "ui.checkout_paused")]
    public async Task FeeRefreshOrExpiryDuringWalletLookupCannotApproveAnOutdatedInvoice(bool expires, string expected)
    {
        var fixture = new Fixture();
        fixture.AfterBalanceRead = () =>
        {
            if (expires) fixture.Revenue.Ready = false;
            else fixture.Revenue.Retention = 0.85m;
        };
        Assert.Equal(expected, await fixture.Shop().ValidateCheckoutAsync(42, "order", "XTR", 107, default));
        Assert.Equal(1, fixture.BalanceReads);
    }

    [Fact]
    public async Task UnknownFeeStopsNewSalesButDoesNotDropAnAlreadyConfirmedPayment()
    {
        var fixture = new Fixture();
        fixture.Revenue.Ready = false;
        var shop = fixture.Shop();
        Assert.False(shop.Readiness.CanBuy);
        Assert.True(shop.Readiness.CanBrowse);
        Assert.Equal("ui.checkout_paused", await shop.ValidateCheckoutAsync(42, "order", "XTR", 107, default));
        Assert.True((await shop.RecordPaymentAsync(42, "order", "charge", "XTR", 107, default)).Accepted);
        Assert.Equal(1, fixture.RecordedPayments);
    }

    [Fact]
    public async Task ExplicitPauseDoesNotDropAnAlreadyConfirmedPayment()
    {
        var fixture = new Fixture();
        var shop = fixture.Shop(new SalesOptions { Enabled = false });
        Assert.False(shop.Readiness.CanBuy);
        Assert.True((await shop.RecordPaymentAsync(42, "order", "charge", "XTR", 107, default)).Accepted);
        Assert.Equal(1, fixture.RecordedPayments);
    }

    [Fact]
    public async Task QuoteRecordsBuiltInTermsAndCalculatedPriceWithoutOptionalSettings()
    {
        var fixture = new Fixture();
        await fixture.Shop().QuoteAsync(42, "package", "request", default);
        Assert.Equal(SalesOptions.BuiltInTermsVersion, fixture.QuotedTerms);
        Assert.Equal(107, fixture.QuotedStars);
    }

    private static IConfiguration Config(string? key = null, string? value = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:EsimBot"] = "Server=localhost;Database=esim_bot;User ID=esim_bot_app;Password=unit-test;SslMode=Required;",
            ["Storage:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["TelegramBot:Token"] = "unit-token", ["TelegramBot:AdminUserId"] = "42",
            ["Provider:AccessCode"] = "unit-access-code"
        };
        if (key is not null) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static EsimPackage Package() => new("package", "Plan", 10000, "USD", 1073741824,
        7, "DAY", [new Country("AZ", "Azerbaijan")], "", "", "", "");
    private static Order Quote() => new("order", 42, "package", "Plan", 10000, 107, "quoted",
        null, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), 0, null);

    private sealed class Revenue : IStarRevenueService
    {
        public bool Ready { get; set; } = true;
        public decimal Retention { get; set; } = 1m;
        public bool IsReady => Ready;
        public decimal RevenueRetention => Ready ? Retention : throw new InvalidOperationException();
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Fixture
    {
        public long Balance { get; init; } = 10000;
        public bool BalanceFails { get; init; }
        public Action? AfterBalanceRead { get; set; }
        public int BalanceReads { get; private set; }
        public int RecordedPayments { get; private set; }
        public int QuotedStars { get; private set; }
        public string? QuotedTerms { get; private set; }
        public Revenue Revenue { get; } = new();

        public ShopService Shop(SalesOptions? config = null)
        {
            var options = Options.Create(config ?? new SalesOptions());
            return new ShopService(Proxy<IEsimAccessClient>((name, _) => name switch
            {
                "get_IsConfigured" => true,
                nameof(IEsimAccessClient.GetBalanceUnitsAsync) => ReadBalance(),
                nameof(IEsimAccessClient.GetPackagesAsync) => Task.FromResult<IReadOnlyList<EsimPackage>>([Package()]),
                _ => throw new InvalidOperationException("Unexpected provider operation.")
            }), Proxy<IOrderService>((name, args) => name switch
            {
                nameof(IOrderService.GetAsync) => Task.FromResult<Order?>(Quote()),
                nameof(IOrderService.RecordPaymentAsync) => RecordPayment(),
                nameof(IOrderService.CreateQuoteAsync) => RecordQuote(args),
                _ => throw new InvalidOperationException("Unexpected store operation.")
            }), new Pricing(options, Revenue), options, Options.Create(new BotOptions { AdminUserId = 1 }),
                Proxy<IBotNotifier>((_, _) => throw new InvalidOperationException("Unexpected notification.")),
                NullLogger<ShopService>.Instance, Revenue);
        }

        private Task<long> ReadBalance()
        {
            BalanceReads++;
            AfterBalanceRead?.Invoke();
            return BalanceFails ? Task.FromException<long>(new HttpRequestException()) : Task.FromResult(Balance);
        }
        private Task<PaymentResult> RecordPayment()
        {
            RecordedPayments++;
            return Task.FromResult(new PaymentResult(true, false, false));
        }
        private Task<Order> RecordQuote(object?[] args)
        {
            QuotedStars = (int)args[2]!;
            QuotedTerms = (string)args[3]!;
            return Task.FromResult(Quote());
        }
    }

    private static T Proxy<T>(Func<string, object?[], object?> call) where T : class
    {
        var result = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)result).Call = call;
        return result;
    }
    public class TestProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args ?? []);
    }
}

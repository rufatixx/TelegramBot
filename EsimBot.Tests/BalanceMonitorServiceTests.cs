using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace EsimBot.Tests;

public sealed class BalanceMonitorServiceTests
{
    [Theory]
    [InlineData("ru")]
    [InlineData("az")]
    [InlineData("en")]
    public async Task Private_alert_reports_fresh_balance_required_cost_and_exact_shortfall(string language)
    {
        await using var f = new Fixture(language: language);
        f.Provider.Balance = 20000;
        f.Service.ReportInsufficient(125123, 0);
        await f.StartAsync();

        var message = Assert.Single(f.Http.Messages);
        Assert.Equal(Fixture.Admin, message.GetProperty("chat_id").GetInt64());
        Assert.Equal(new BotText(language).Text("balance.order", "$2.00", "$12.5123", "$10.5123"), message.GetProperty("text").GetString());
        Assert.True(message.GetProperty("protect_content").GetBoolean());
        Assert.False(message.TryGetProperty("parse_mode", out _));
        Assert.Single(f.State.Values);
    }

    [Fact]
    public async Task Ten_thousand_parallel_reports_are_nonblocking_and_coalesce_to_largest_shortfall()
    {
        await using var f = new Fixture();
        f.Provider.Balance = 2500;
        Parallel.For(1, 10001, number => f.Service.ReportInsufficient(10000 + number, 0));
        Assert.Equal(0, f.Provider.Calls);
        Assert.Equal(0, f.State.Reads);
        Assert.Empty(f.Http.Messages);

        await f.StartAsync();

        Assert.Equal(new BotText("en").Text("balance.order", "$0.25", "$2.00", "$1.75"),
            Assert.Single(f.Http.Messages).GetProperty("text").GetString());
        Assert.InRange(f.Provider.Calls, 1, 2);
    }

    [Fact]
    public async Task Periodic_zero_balance_alert_explains_minimum_for_cheapest_available_plan()
    {
        await using var f = new Fixture();
        f.Provider.Balance = 0;
        f.Catalog.Costs = [80000, 13500, 70000];
        await f.StartAsync();

        Assert.Equal(new BotText("en").Text("balance.minimum", "$0.00", "$1.35", "$1.35"),
            Assert.Single(f.Http.Messages).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Positive_balance_below_cheapest_plan_also_reports_minimum_shortfall()
    {
        await using var f = new Fixture();
        f.Provider.Balance = 1000;
        await f.StartAsync();
        Assert.Equal(new BotText("en").Text("balance.minimum", "$0.10", "$1.00", "$0.90"),
            Assert.Single(f.Http.Messages).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Empty_balance_is_reported_even_when_catalogue_is_unavailable_without_inventing_an_amount()
    {
        await using var f = new Fixture();
        f.Provider.Balance = 0;
        f.Catalog.Fail = true;
        await f.StartAsync();
        Assert.Equal(new BotText("en").Text("balance.empty"), Assert.Single(f.Http.Messages).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Topup_before_delivery_discards_stale_checkout_warning()
    {
        await using var f = new Fixture();
        f.Service.ReportInsufficient(100000, 0);
        f.Provider.Balance = 100000;
        await f.StartAsync();
        Assert.Empty(f.Http.Messages);
        Assert.Empty(f.State.Values);
    }

    [Fact]
    public async Task Polling_detects_exhaustion_without_any_customer_request()
    {
        await using var f = new Fixture();
        await f.StartAsync();
        Assert.Empty(f.Http.Messages);
        f.Provider.Balance = 0;

        await f.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Single(f.Http.Messages);
    }

    [Fact]
    public async Task Repeated_requests_wait_five_minutes_and_keep_the_largest_requested_plan()
    {
        await using var f = new Fixture();
        f.Service.ReportInsufficient(100000, 0);
        await f.StartAsync();
        for (var index = 0; index < 1000; index++) f.Service.ReportInsufficient(500000 + index, 0);

        await f.AdvanceAsync(TimeSpan.FromMinutes(4));
        Assert.Single(f.Http.Messages);
        f.Provider.Balance = 450000;
        await f.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(2, f.Http.Messages.Count);
        Assert.Equal(new BotText("en").Text("balance.order", "$45.00", "$50.0999", "$5.0999"),
            f.Http.Messages.Last().GetProperty("text").GetString());
    }

    [Fact]
    public async Task Persisted_cooldown_survives_restart()
    {
        await using var f = new Fixture();
        f.Provider.Balance = 0;
        await f.StartAsync();
        Assert.Single(f.Http.Messages);
        await f.RestartAsync();
        Assert.Single(f.Http.Messages);
        await f.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Single(f.Http.Messages);
        f.Provider.Balance = 1000;
        await f.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(2, f.Http.Messages.Count);
    }

    [Fact]
    public async Task Recovery_clears_warning_and_future_exhaustion_is_reported_again()
    {
        await using var f = new Fixture();
        f.Provider.Balance = 0;
        await f.StartAsync();
        f.Provider.Balance = 50000;
        await f.AdvanceAsync(TimeSpan.FromMinutes(5));
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("null", Assert.Single(f.State.Values).Value);
        f.Provider.Balance = 0;
        f.Service.ReportInsufficient(20000, 0);
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, f.Http.Messages.Count);
    }

    [Fact]
    public async Task Blocked_admin_retries_slowly_and_preserves_pending_warning()
    {
        await using var f = new Fixture();
        f.Http.ErrorCode = 403;
        f.Provider.Balance = 0;
        await f.StartAsync();
        Assert.Single(f.Http.Messages);
        Assert.Empty(f.State.Values);
        await f.AdvanceAsync(TimeSpan.FromMinutes(4));
        Assert.Single(f.Http.Messages);
        f.Http.ErrorCode = 0;
        await f.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, f.Http.Messages.Count);
        Assert.Single(f.State.Values);
        Assert.All(f.Log.Messages, message => Assert.DoesNotContain("sensitive-token", message));
        Assert.All(f.Log.Exceptions, Assert.Null);
    }

    [Fact]
    public async Task Telegram_rate_limit_honors_retry_after()
    {
        await using var f = new Fixture();
        f.Http.ErrorCode = 429;
        f.Provider.Balance = 0;
        await f.StartAsync();
        await f.AdvanceAsync(TimeSpan.FromSeconds(44));
        Assert.Single(f.Http.Messages);
        f.Http.ErrorCode = 0;
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, f.Http.Messages.Count);
    }

    [Fact]
    public async Task Provider_failure_does_not_turn_an_unknown_balance_into_zero()
    {
        await using var f = new Fixture();
        f.Provider.Fail = true;
        f.Service.ReportInsufficient(100000, 0);
        await f.StartAsync();
        Assert.Empty(f.Http.Messages);
        f.Provider.Fail = false;
        await f.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Single(f.Http.Messages);
        Assert.All(f.Log.Messages, message => Assert.DoesNotContain("sensitive-token", message));
        Assert.All(f.Log.Exceptions, Assert.Null);
    }

    [Fact]
    public async Task Failed_deduplication_write_does_not_repeat_a_successful_message()
    {
        await using var f = new Fixture();
        f.State.FailWrites = true;
        f.Provider.Balance = 0;
        await f.StartAsync();
        f.Service.ReportInsufficient(100000, 0);
        await f.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Single(f.Http.Messages);
        Assert.Equal(1, f.State.Writes);
        f.State.FailWrites = false;
        await f.AdvanceAsync(TimeSpan.FromSeconds(29));
        Assert.Single(f.Http.Messages);
        Assert.Single(f.State.Values);
    }

    [Theory]
    [InlineData(true, false, 99)]
    [InlineData(false, true, 99)]
    [InlineData(false, false, 0)]
    public async Task Test_environments_and_missing_admin_never_contact_provider_or_telegram(bool botTest, bool paymentTest, long admin)
    {
        await using var f = new Fixture(botTest, paymentTest, admin);
        f.Service.ReportInsufficient(100000, 0);
        await f.StartAsync();
        Assert.Empty(f.Http.Messages);
        Assert.Equal(0, f.Provider.Calls);
        Assert.Equal(0, f.State.Reads);
        Assert.Equal(0, f.Catalog.Calls);
    }

    [Fact]
    public async Task Invalid_reports_are_ignored()
    {
        await using var f = new Fixture();
        f.Service.ReportInsufficient(0, 0);
        f.Service.ReportInsufficient(-1, 0);
        f.Service.ReportInsufficient(1, -1);
        f.Service.ReportInsufficient(20000, 20000);
        f.Service.ReportInsufficient(20000, 20001);
        await f.StartAsync();
        Assert.Empty(f.Http.Messages);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const long Admin = 99;
        public FakeProvider Provider { get; } = new();
        public FakeCatalog Catalog { get; } = new();
        public FakeState State { get; } = new();
        public FakeTelegram Http { get; } = new();
        public CaptureLogger Log { get; } = new();
        private readonly ManualTime _time = new();
        private readonly IOptions<BotOptions> _options;
        private readonly IOptions<PaymentTestOptions> _testing;
        private readonly FakeLanguages _languages;
        private readonly HttpClient _client;
        private readonly ITelegramBotClient _bot;
        private CancellationTokenSource _stop = new();
        private Task? _run;
        public BalanceMonitorService Service { get; private set; }

        public Fixture(bool botTest = false, bool paymentTest = false, long admin = Admin, string language = "en")
        {
            _options = Options.Create(new BotOptions { AdminUserId = admin, TestEnvironment = botTest });
            _testing = Options.Create(new PaymentTestOptions { Enabled = paymentTest });
            _languages = new FakeLanguages(language);
            _client = new HttpClient(Http);
            _bot = new TelegramBotClient(new TelegramBotClientOptions("123456:FAKE_TEST_TOKEN")
                { RetryThreshold = 0, RetryCount = 0 }, _client);
            Service = NewService();
        }
        private BalanceMonitorService NewService() => new(Provider, Catalog, _bot, _languages, State, _options, _testing, Log, _time);
        public async Task StartAsync()
        {
            _run = Service.RunAsync(_stop.Token);
            await WaitUntilAsync(() => _run.IsCompleted || _time.HasTimer);
            if (_run.IsFaulted) await _run;
        }
        public async Task AdvanceAsync(TimeSpan amount)
        {
            _time.Advance(amount);
            await WaitUntilAsync(() => _run!.IsCompleted || _time.HasTimer);
            if (_run!.IsFaulted) await _run;
        }
        public async Task RestartAsync()
        {
            await _stop.CancelAsync();
            if (_run is not null) await _run;
            _stop.Dispose();
            _stop = new CancellationTokenSource();
            Service = NewService();
            await StartAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (_run is not null) await _run;
            _stop.Dispose();
            _client.Dispose();
        }
        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition()) await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class FakeProvider : IEsimAccessClient
    {
        public bool IsConfigured => true;
        public long Balance { get; set; } = 20000;
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task<long> GetBalanceUnitsAsync(CancellationToken ct)
        {
            Calls++;
            return Fail ? Task.FromException<long>(new HttpRequestException("sensitive-token")) : Task.FromResult(Balance);
        }
        public Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct) => throw new InvalidOperationException("Use catalogue service.");
        public Task<string> PlaceOrderAsync(string transactionId, string packageCode, long priceUnits, CancellationToken ct) => throw new InvalidOperationException("No purchase allowed.");
        public Task<ProviderOrder?> QueryOrderAsync(string transactionId, string? orderNumber, CancellationToken ct) => throw new InvalidOperationException("No purchase allowed.");
        public Task<EsimUsage> GetUsageAsync(string esimId, CancellationToken ct) => throw new InvalidOperationException("No profile allowed.");
    }

    private sealed class FakeCatalog : ICatalogService
    {
        public long[] Costs { get; set; } = [10000];
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct)
        {
            Calls++;
            return Fail ? Task.FromException<IReadOnlyList<EsimPackage>>(new InvalidOperationException("sensitive-token"))
                : Task.FromResult<IReadOnlyList<EsimPackage>>(Costs.Select((cost, index) => new EsimPackage(
                    index.ToString(CultureInfo.InvariantCulture), "Plan", cost, "USD", 1024, 1, "DAY", [], "", "install", "", "1")).ToArray());
        }
    }

    private sealed class FakeLanguages(string language) : ILanguageService
    {
        public Task<BotText> GetAsync(long userId, string? telegramLanguageCode, CancellationToken ct)
        { Assert.Equal(Fixture.Admin, userId); return Task.FromResult(new BotText(language)); }
        public Task SetAsync(long userId, string language, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeState : IAppStateRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool FailWrites { get; set; }
        public Task<string?> GetAsync(string key, CancellationToken ct)
        { Reads++; return Task.FromResult(Values.GetValueOrDefault(key)); }
        public Task SetAsync(string key, string value, CancellationToken ct)
        {
            Writes++;
            if (FailWrites) throw new InvalidOperationException("sensitive-token");
            Values[key] = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct) { Values.Remove(key); return Task.CompletedTask; }
        public Task DeleteExpiredSupportStateAsync(long nowUnixSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task VerifyAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task EnsureEnvironmentAsync(string environment, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeTelegram : HttpMessageHandler
    {
        public ConcurrentQueue<JsonElement> Messages { get; } = new();
        public int ErrorCode { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.EndsWith("/sendMessage", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement.Clone();
            Messages.Enqueue(body);
            var result = ErrorCode == 0
                ? JsonSerializer.Serialize(new { ok = true, result = new { message_id = Messages.Count, date = 0,
                    chat = new { id = Fixture.Admin, type = "private" } } })
                : JsonSerializer.Serialize(new { ok = false, error_code = ErrorCode, description = "sensitive-token",
                    parameters = new { retry_after = 45 } });
            return new HttpResponseMessage(ErrorCode == 0 ? HttpStatusCode.OK : (HttpStatusCode)ErrorCode)
            { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CaptureLogger : ILogger<BalanceMonitorService>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, exception)); Exceptions.Add(exception); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public bool HasTimer { get { lock (_gate) return _timers.Count > 0; } }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += amount;
                due = _timers.Where(timer => timer.Due <= _now).ToArray();
                foreach (var timer in due) _timers.Remove(timer);
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset Due { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    { Due = owner._now + dueTime; owner._timers.Add(this); }
                }
                return true;
            }
            public void Dispose() { lock (owner._gate) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

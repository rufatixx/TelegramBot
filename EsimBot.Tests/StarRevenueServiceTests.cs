using System.Net;
using System.Text;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EsimBot.Tests;

public sealed class StarRevenueServiceTests
{
    [Theory]
    [InlineData("true", "0.85")]
    [InlineData("false", "1")]
    [InlineData(null, "1")]
    public async Task TopicsModeIsReadFromRawGetMeWithoutRelyingOnOldSdk(string? topics, string expected)
    {
        using var http = new StubHandler(Account(topics));
        var factory = new TestFactory(http);
        var service = Service(factory);
        Assert.False(service.IsReady);
        Assert.Throws<InvalidOperationException>(() => service.RevenueRetention);

        await service.RefreshAsync(default);

        Assert.True(service.IsReady);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), service.RevenueRetention);
        Assert.Equal("Telegram", Assert.Single(factory.ClientNames));
        Assert.Equal("GET", Assert.Single(http.Requests).Method);
        Assert.Equal("https://api.telegram.org/bot123456:FAKE_TEST_TOKEN/getMe", http.Requests[0].Url);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"description\":\"private error\"}")]
    [InlineData("{\"ok\":true,\"result\":{\"id\":1,\"is_bot\":false}}")]
    [InlineData("{\"ok\":true,\"result\":{\"id\":0,\"is_bot\":true}}")]
    [InlineData("{\"ok\":true,\"result\":{\"id\":-1,\"is_bot\":true}}")]
    [InlineData("{\"ok\":true,\"result\":{\"id\":\"1\",\"is_bot\":true}}")]
    [InlineData("{\"ok\":true,\"result\":{\"id\":1,\"is_bot\":true,\"has_topics_enabled\":null}}")]
    [InlineData("{\"ok\":true,\"result\":{\"id\":1,\"is_bot\":true,\"has_topics_enabled\":\"false\"}}")]
    [InlineData("{\"ok\":true,\"result\":{\"id\":1}}")]
    [InlineData("{bad-json")]
    [InlineData("null")]
    public async Task UnknownOrMalformedApiConditionsFailClosed(string json)
    {
        using var http = new StubHandler(Json(json));
        var service = Service(new TestFactory(http));

        await service.RefreshAsync(default);

        Assert.False(service.IsReady);
        Assert.Throws<InvalidOperationException>(() => service.RevenueRetention);
    }

    [Fact]
    public async Task HttpFailureRetainsSnapshotOnlyUntilTwoMinuteExpiry()
    {
        var time = new ManualTime();
        using var http = new StubHandler(Account("true"), new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = Service(new TestFactory(http), time);
        await service.RefreshAsync(default);
        time.Advance(TimeSpan.FromSeconds(60));

        await service.RefreshAsync(default);

        Assert.True(service.IsReady);
        Assert.Equal(0.85m, service.RevenueRetention);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(service.IsReady);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(service.IsReady);
        Assert.Throws<InvalidOperationException>(() => service.RevenueRetention);
    }

    [Fact]
    public async Task SuccessfulRefreshReplacesFeeAndExtendsSnapshotExpiry()
    {
        var time = new ManualTime();
        using var http = new StubHandler(Account("false"), Account("true"));
        var service = Service(new TestFactory(http), time);
        await service.RefreshAsync(default);
        time.Advance(TimeSpan.FromSeconds(119));

        await service.RefreshAsync(default);

        Assert.Equal(0.85m, service.RevenueRetention);
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.True(service.IsReady);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task TestingNeverCreatesHttpClientOrTouchesProductionEvenWithToken()
    {
        using var http = new StubHandler();
        var factory = new TestFactory(http);
        var service = Service(factory, testing: true);

        await service.RefreshAsync(default);
        await service.RunAsync(default);

        Assert.Empty(factory.ClientNames);
        Assert.Empty(http.Requests);
        Assert.False(service.IsReady);
        Assert.Throws<InvalidOperationException>(() => service.RevenueRetention);
    }

    [Fact]
    public async Task MissingTokenDoesNotMakeAnHttpRequest()
    {
        using var http = new StubHandler();
        var factory = new TestFactory(http);
        var service = new StarRevenueService(factory, Options.Create(new BotOptions()), NullLogger<StarRevenueService>.Instance);

        await service.RefreshAsync(default);

        Assert.Empty(factory.ClientNames);
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task TransportErrorsDoNotLeakTokenOrExceptionMessageIntoLogs()
    {
        using var http = new ThrowingHandler(new HttpRequestException("secret-url-and-token"));
        var logger = new CaptureLogger();
        var service = Service(new TestFactory(http), logger: logger);

        await service.RefreshAsync(default);

        Assert.False(service.IsReady);
        Assert.All(logger.Messages, message => Assert.DoesNotContain("secret-url-and-token", message));
        Assert.All(logger.Messages, message => Assert.DoesNotContain("FAKE_TEST_TOKEN", message));
        Assert.All(logger.Exceptions, Assert.Null);
        Assert.Contains("HttpRequestException", Assert.Single(logger.Messages));
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowed()
    {
        using var stop = new CancellationTokenSource();
        using var http = new CancelingHandler(stop);
        var service = Service(new TestFactory(http));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(stop.Token));

        Assert.False(service.IsReady);
    }

    private static StarRevenueService Service(TestFactory factory, TimeProvider? time = null,
        bool testing = false, ILogger<StarRevenueService>? logger = null) => new(factory,
        Options.Create(new BotOptions { Token = "123456:FAKE_TEST_TOKEN", TestEnvironment = testing }),
        logger ?? NullLogger<StarRevenueService>.Instance, time);

    private static HttpResponseMessage Account(string? topics) => Json("{\"ok\":true,\"result\":{\"id\":123456,\"is_bot\":true"
        + (topics is null ? "" : ",\"has_topics_enabled\":" + topics) + "}}");
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
    }

    private sealed class TestFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> ClientNames { get; } = [];
        public HttpClient CreateClient(string name)
        { ClientNames.Add(name); return new HttpClient(handler, disposeHandler: false); }
    }

    private sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<(string Method, string Url)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri)); return Task.FromResult(_responses.Dequeue()); }
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(error);
    }

    private sealed class CancelingHandler(CancellationTokenSource stop) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { stop.Cancel(); return Task.FromCanceled<HttpResponseMessage>(ct); }
    }

    private sealed class CaptureLogger : ILogger<StarRevenueService>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, exception)); Exceptions.Add(exception); }
    }
}

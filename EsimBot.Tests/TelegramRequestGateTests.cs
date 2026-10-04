using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using EsimBot.BLL.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace EsimBot.Tests;

public sealed class TelegramRequestGateTests
{
    [Fact]
    public async Task GlobalStartsArePacedAndDifferentChatsCanOverlapHttp()
    {
        var gate = new TelegramRequestGate();
        var started = new ConcurrentQueue<long>();
        var leases = await Task.WhenAll(Enumerable.Range(1, 20).Select(async chat =>
        {
            var lease = await gate.AcquireAsync(chat, default);
            started.Enqueue(Stopwatch.GetTimestamp());
            return lease;
        }));
        try
        {
            var timestamps = started.ToArray();
            Assert.Equal(20, timestamps.Length);
            Assert.True(Stopwatch.GetElapsedTime(timestamps[0], timestamps[^1]) >= TimeSpan.FromMilliseconds(730));
            for (var i = 1; i < timestamps.Length; i++)
                Assert.True(Stopwatch.GetElapsedTime(timestamps[i - 1], timestamps[i]) >= TimeSpan.FromMilliseconds(30));
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public async Task SameChatHasOneSecondCooldownWhileOtherChatsContinue()
    {
        var gate = new TelegramRequestGate();
        var first = await gate.AcquireAsync(1, default);
        var started = Stopwatch.GetTimestamp();
        first.Dispose();
        var next = gate.AcquireAsync(1, default).AsTask();
        using var other = await gate.AcquireAsync(2, default);
        Assert.False(next.IsCompleted);
        using var second = await next;
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(980));
    }

    [Fact]
    public async Task ExcessAdmissionFailsPromptlyAndCancelledWaitersReturnPermits()
    {
        var gate = new TelegramRequestGate();
        var first = await gate.AcquireAsync(1, default);
        using var cancellation = new CancellationTokenSource();
        var queued = Enumerable.Range(0, 127).Select(_ => gate.AcquireAsync(1, cancellation.Token).AsTask()).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.AcquireAsync(2, default).AsTask());
        cancellation.Cancel();
        foreach (var pending in queued)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        first.Dispose();
        first.Dispose(); // A duplicate dispose must not over-release either semaphore.
        using var recovered = await gate.AcquireAsync(2, default);
    }

    [Fact]
    public async Task HistoricalChatsAreEvictedInsteadOfGrowingPerUserStateForever()
    {
        var gate = new TelegramRequestGate(new AdvancingClock());
        for (var chat = 1; chat <= 5000; chat++)
        {
            using var lease = await gate.AcquireAsync(chat, default);
        }
        var chats = (IDictionary)typeof(TelegramRequestGate).GetField("_chats", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gate)!;
        Assert.InRange(chats.Count, 1, 256);
    }

    [Theory]
    [InlineData("getUpdates")]
    [InlineData("answerPreCheckoutQuery")]
    [InlineData("answerCallbackQuery")]
    [InlineData("refundStarPayment")]
    [InlineData("getMe")]
    [InlineData("setMyCommands")]
    [InlineData("sendChatAction")]
    public async Task ControlAndPaymentCallsBypassAStalledMessageQueue(string method)
    {
        var gate = new FakeGate { Block = true };
        using var downstream = new StubHandler();
        using var client = Http(gate, downstream);
        using var cancellation = new CancellationTokenSource();
        var message = client.PostAsync(Url("sendMessage"), Json("""{"chat_id":123,"text":"hello"}"""), cancellation.Token);
        await gate.Entered.Task;
        using var reply = await client.PostAsync(Url(method), Json("{}"));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(1, downstream.Calls);
        Assert.False(message.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => message);
        Assert.Equal([123L], gate.Chats.ToArray());
    }

    [Theory]
    [InlineData("sendMessage")]
    [InlineData("sendPhoto")]
    [InlineData("sendInvoice")]
    [InlineData("copyMessage")]
    [InlineData("editMessageText")]
    public async Task MessagePayloadRemainsUnchangedAndEachWriteIsAttemptedOnlyOnce(string method)
    {
        const string payload = """{"chat_id":"123","text":"quoted \\\" value"}""";
        var gate = new FakeGate();
        using var downstream = new StubHandler { ReadJson = true, Status = HttpStatusCode.TooManyRequests };
        using var client = Http(gate, downstream);
        using var response = await client.PostAsync(Url(method), Json(payload));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, downstream.Calls);
        Assert.Equal(payload, downstream.Body);
        Assert.Equal([123L], gate.Chats.ToArray());
        Assert.Equal(1, gate.Disposed);
    }

    [Fact]
    public async Task ActualSdkMessageRequestIsRecognizedAndPreservesText()
    {
        var gate = new FakeGate();
        using var downstream = new StubHandler { ReadJson = true };
        using var client = Http(gate, downstream);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:FAKE_TEST_TOKEN")
            { RetryThreshold = 0, RetryCount = 0 }, client);
        await bot.SendMessage(123, "Здравствуйте! ✈");
        Assert.Equal([123L], gate.Chats.ToArray());
        Assert.Equal(1, downstream.Calls);
        using var payload = System.Text.Json.JsonDocument.Parse(downstream.Body!);
        Assert.Equal("Здравствуйте! ✈", payload.RootElement.GetProperty("text").GetString());
        Assert.Equal(1, gate.Disposed);
    }

    [Fact]
    public async Task ActualSdkPhotoRequestInspectsOnlyChatIdWithoutReadingFileStream()
    {
        var gate = new FakeGate();
        using var downstream = new StubHandler();
        using var client = Http(gate, downstream);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:FAKE_TEST_TOKEN")
            { RetryThreshold = 0, RetryCount = 0 }, client);
        using var forbidden = new UnreadableFileStream();
        await bot.SendPhoto(123, InputFile.FromStream(forbidden, "private.png"));
        Assert.Equal([123L], gate.Chats.ToArray());
        Assert.Equal(1, downstream.Calls);
        Assert.Equal(0, forbidden.ReadAttempts);
        Assert.Equal(1, gate.Disposed);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"chat_id\":{\"secret\":\"never expose\"}}")]
    public async Task MalformedMessageCannotBypassGateOrExposePayload(string payload)
    {
        var gate = new FakeGate();
        using var downstream = new StubHandler();
        using var client = Http(gate, downstream);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync(Url("sendMessage"), Json(payload)));
        Assert.DoesNotContain(payload, error.Message);
        Assert.Empty(gate.Chats);
        Assert.Equal(0, downstream.Calls);
    }

    private static string Url(string method) => "https://api.telegram.org/bot123456:FAKE_TEST_TOKEN/" + method;
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
    private static HttpClient Http(ITelegramRequestGate gate, HttpMessageHandler downstream)
    {
        var type = typeof(TelegramRequestGate).Assembly.GetType("EsimBot.BLL.Services.TelegramRateLimitHandler", true)!;
        var handler = (DelegatingHandler)Activator.CreateInstance(type, gate)!;
        handler.InnerHandler = downstream;
        return new HttpClient(handler);
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, 100);
    }

    private sealed class FakeGate : ITelegramRequestGate
    {
        public bool Block { get; init; }
        public ConcurrentQueue<long> Chats { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposed;
        public async ValueTask<IDisposable> AcquireAsync(long chatId, CancellationToken ct)
        {
            Chats.Enqueue(chatId);
            Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new Disposal(this);
        }
        private sealed class Disposal(FakeGate gate) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref gate.Disposed);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls;
        public bool ReadJson { get; init; }
        public string? Body;
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (ReadJson) Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status)
            {
                Content = Json(Status == HttpStatusCode.OK
                    ? """{"ok":true,"result":{"message_id":1,"date":1700000000,"chat":{"id":123,"type":"private"}}}"""
                    : """{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":3}}""")
            };
        }
    }

    private sealed class UnreadableFileStream : Stream
    {
        public int ReadAttempts;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { ReadAttempts++; throw new InvalidOperationException("File must not be read by limiter."); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ReadAttempts++; throw new InvalidOperationException("File must not be read by limiter."); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

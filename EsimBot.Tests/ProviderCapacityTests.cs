using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Options;

namespace EsimBot.Tests;

[CollectionDefinition("Provider capacity", DisableParallelization = true)]
public sealed class ProviderCapacityCollection;

// Isolate timing assertions from unrelated tests using the process-wide provider rate limit.
[Collection("Provider capacity")]
public sealed class ProviderCapacityTests
{
    [Fact]
    public async Task ConcurrentSignedRequestsKeepPacedStartsAndAtMostFourInFlight()
    {
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        var requests = Enumerable.Range(0, 4).Select(_ => client.GetBalanceUnitsAsync(default)).ToArray();
        try
        {
            await handler.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, handler.InFlight);
            var timestamps = handler.Started.ToArray();
            for (var i = 1; i < timestamps.Length; i++)
                Assert.True(Stopwatch.GetElapsedTime(timestamps[i - 1], timestamps[i]) >= TimeSpan.FromMilliseconds(120));
            Assert.Equal(4, handler.RequestIds.Distinct().Count());
        }
        finally { handler.Release.TrySetResult(); }
        Assert.All(await Task.WhenAll(requests), balance => Assert.Equal(1000000, balance));
        Assert.Equal(0, handler.InFlight);
    }

    [Fact]
    public async Task BurstHasBoundedAdmissionAndQueueTimeoutWithoutHiddenReadRetries()
    {
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        var requests = Enumerable.Range(0, 100).Select(async _ =>
        {
            try { await client.GetBalanceUnitsAsync(default); return "ok"; }
            catch (ProviderException ex) { return ex.Code; }
        }).ToArray();
        try
        {
            // At least the excess beyond the bounded 32 admissions fails without waiting for I/O.
            Assert.True(requests.Count(task => task.IsCompleted) >= 68);
            await handler.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(2300));
            Assert.Equal(4, handler.Calls);
            Assert.Equal(96, requests.Count(task => task.IsCompleted));
            Assert.All(requests.Where(task => task.IsCompleted), task => Assert.Equal("provider_busy", task.Result));
        }
        finally { handler.Release.TrySetResult(); }
        var results = await Task.WhenAll(requests);
        Assert.Equal(4, results.Count(result => result == "ok"));
        Assert.Equal(1000000, await client.GetBalanceUnitsAsync(default));
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task CancellationReturnsAllPermitsAndDoesNotRetryCancelledRequests()
    {
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var client = Client(http);
        using var cancellation = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 8).Select(_ => client.GetBalanceUnitsAsync(cancellation.Token)).ToArray();
        await handler.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        foreach (var request in requests)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(0, handler.InFlight);
        handler.Release.TrySetResult();
        Assert.Equal(1000000, await client.GetBalanceUnitsAsync(default));
    }

    [Fact]
    public async Task FailedPurchaseIsSignedOnceAndDoesNotRetryInsideClient()
    {
        using var handler = new BlockingHandler { Status = HttpStatusCode.ServiceUnavailable };
        handler.Release.SetResult();
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ProviderException>(() => Client(http).PlaceOrderAsync("stable-order", "AZ1", 10000, default));
        Assert.Equal("http_503", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(1, handler.Calls);
        Assert.Single(handler.RequestIds);
    }

    private static EsimAccessClient Client(HttpClient http) => new(http,
        Options.Create(new ProviderOptions { AccessCode = "capacity-fake-access", SecretKey = "capacity-fake-secret" }),
        Options.Create(new PaymentTestOptions()));

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private int _calls;
        private int _inFlight;
        public int Calls => Volatile.Read(ref _calls);
        public int InFlight => Volatile.Read(ref _inFlight);
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<long> Started { get; } = new();
        public ConcurrentQueue<string> RequestIds { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.Enqueue(Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _calls);
            var count = Interlocked.Increment(ref _inFlight);
            try
            {
                var id = request.Headers.GetValues("RT-RequestID").Single();
                var timestamp = request.Headers.GetValues("RT-Timestamp").Single();
                var body = await request.Content!.ReadAsByteArrayAsync(ct);
                var prefix = Encoding.UTF8.GetBytes(timestamp + id + "capacity-fake-access");
                var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes("capacity-fake-secret"), prefix.Concat(body).ToArray());
                Assert.Equal(Convert.ToHexString(signature).ToLowerInvariant(), request.Headers.GetValues("RT-Signature").Single());
                RequestIds.Enqueue(id);
                if (count == 4) FourStarted.TrySetResult();
                await Release.Task.WaitAsync(ct);
                return new HttpResponseMessage(Status)
                {
                    Content = new StringContent("""{"success":true,"obj":{"balance":1000000}}""", Encoding.UTF8, "application/json")
                };
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }
}

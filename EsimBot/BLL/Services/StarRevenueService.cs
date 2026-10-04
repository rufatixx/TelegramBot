using System.Text.Json;
using EsimBot.BLL.DTO;
using Microsoft.Extensions.Options;

namespace EsimBot.BLL.Services;

/// <summary>Reads the bot's current topics mode; this is not an API for guaranteed USD withdrawal proceeds.</summary>
public sealed class StarRevenueService(IHttpClientFactory httpClientFactory, IOptions<BotOptions> options,
    ILogger<StarRevenueService> logger, TimeProvider? timeProvider = null) : IStarRevenueService
{
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(2);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Snapshot? _snapshot;
    private int _lastRefreshSucceeded;

    public bool IsReady => CurrentSnapshot() is not null;

    public decimal RevenueRetention => CurrentSnapshot()?.Retention
        ?? throw new InvalidOperationException("Telegram revenue conditions are unknown or expired.");

    public async Task RefreshAsync(CancellationToken ct)
    {
        // Test payments must never inspect or contact the production Telegram account.
        if (options.Value.TestEnvironment) return;
        await _refreshGate.WaitAsync(ct);
        try
        {
            var token = options.Value.Token;
            if (string.IsNullOrWhiteSpace(token) || token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)
                || c is '/' or '\\' or '?' or '#'))
                throw new InvalidDataException("Telegram credentials are not configured.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = httpClientFactory.CreateClient("Telegram");
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.telegram.org/bot" + token + "/getMe");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("is_bot", out var isBot)
                || isBot.ValueKind != JsonValueKind.True || !result.TryGetProperty("id", out var id)
                || !id.TryGetInt64(out var botId) || botId <= 0)
                throw new InvalidDataException("Invalid Telegram account response.");

            var topics = false;
            if (result.TryGetProperty("has_topics_enabled", out var flag))
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Invalid Telegram revenue conditions.");
                topics = flag.GetBoolean();
            }
            Volatile.Write(ref _snapshot, new Snapshot(_time.GetTimestamp(), topics ? 0.85m : 1m));
            Volatile.Write(ref _lastRefreshSucceeded, 1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastRefreshSucceeded, 0);
            // The HTTP URL contains the bot token. Never log exceptions, URLs, response bodies or messages.
            logger.LogWarning("Telegram revenue check failed ({ErrorType}); new sales require a fresh snapshot", ex.GetType().Name);
        }
        finally { _refreshGate.Release(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (options.Value.TestEnvironment) return;
        while (!ct.IsCancellationRequested)
        {
            var delay = Volatile.Read(ref _lastRefreshSucceeded) == 1 ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(30);
            await Task.Delay(delay, _time, ct);
            await RefreshAsync(ct);
        }
    }

    private Snapshot? CurrentSnapshot()
    {
        if (options.Value.TestEnvironment) return null;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null) return null;
        var age = _time.GetElapsedTime(snapshot.Timestamp);
        return age >= TimeSpan.Zero && age < SnapshotLifetime ? snapshot : null;
    }

    private sealed record Snapshot(long Timestamp, decimal Retention);
}

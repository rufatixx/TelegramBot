using System.Globalization;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace EsimBot.BLL.Services;

/// <summary>Coalesces low supplier balance into bounded, private administrator notifications.</summary>
public sealed class BalanceMonitorService(IEsimAccessClient provider, ICatalogService catalog,
    ITelegramBotClient bot, ILanguageService languages, IAppStateRepository state,
    IOptions<BotOptions> options, IOptions<PaymentTestOptions> testing,
    ILogger<BalanceMonitorService> logger, TimeProvider? timeProvider = null) : IBalanceMonitorService
{
    private const string StateKey = "balance:admin_alert:v1";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private Notice? _pending;
    private SentNotice? _last;
    private DateTimeOffset _nextCheck;
    private DateTimeOffset _nextAttempt;
    private DateTimeOffset _nextSave;
    private bool _savePending;
    private int _running;

    private bool Enabled => !options.Value.TestEnvironment && !testing.Value.Enabled
        && options.Value.AdminUserId > 0 && provider.IsConfigured;

    public void ReportInsufficient(long requiredUnits, long balanceUnits)
    {
        if (!Enabled || requiredUnits <= 0 || balanceUnits < 0 || balanceUnits >= requiredUnits) return;
        Queue(new Notice(requiredUnits, balanceUnits, "order"));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Enabled || Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            await LoadAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                await SaveAsync(ct);
                if (_time.GetUtcNow() >= _nextCheck) await CheckBalanceAsync(ct);
                if (_time.GetUtcNow() >= _nextAttempt) await SendPendingAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(1), _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Volatile.Write(ref _running, 0); }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var timeout = Timeout(ct);
                var saved = await state.GetAsync(StateKey, timeout.Token);
                if (saved is not null)
                {
                    try { _last = JsonSerializer.Deserialize<SentNotice>(saved); }
                    catch (JsonException) { _last = null; }
                    if (_last is not null && (_last.AdminId != options.Value.AdminUserId
                        || _last.RequiredUnits < 0 || _last.BalanceUnits < 0
                        || _last.Kind is not ("order" or "minimum" or "empty"))) _last = null;
                    // A future timestamp (for example after a clock correction) may delay at most one interval.
                    if (_last is not null)
                        _nextAttempt = Min(_last.SentAt + CheckInterval, _time.GetUtcNow() + CheckInterval);
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LogFailure("load", ex);
                await Task.Delay(RetryInterval, _time, ct);
            }
        }
    }

    private async Task CheckBalanceAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = Timeout(ct);
            var balance = await provider.GetBalanceUnitsAsync(timeout.Token);
            if (balance < 0) throw new InvalidDataException("Invalid supplier balance.");
            RearmIfRecovered(balance);
            var required = 0L;
            try
            {
                var packages = await catalog.GetPackagesAsync(timeout.Token);
                required = packages.Where(package => package.PriceUnits > 0).Select(package => package.PriceUnits)
                    .DefaultIfEmpty(0).Min();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { LogFailure("catalog", ex); }

            if (required > balance) Queue(new Notice(required, balance, "minimum"));
            else if (balance == 0) Queue(new Notice(0, 0, "empty"));
            _nextCheck = _time.GetUtcNow() + CheckInterval;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure("check", ex);
            _nextCheck = _time.GetUtcNow() + RetryInterval;
        }
    }

    private async Task SendPendingAsync(CancellationToken ct)
    {
        Notice? pending;
        lock (_gate) { pending = _pending; _pending = null; }
        if (pending is null) return;
        try
        {
            using var timeout = Timeout(ct);
            // A queued checkout may be minutes old: refresh before reporting an exact shortfall.
            var balance = await provider.GetBalanceUnitsAsync(timeout.Token);
            if (balance < 0) throw new InvalidDataException("Invalid supplier balance.");
            if (pending.RequiredUnits > 0 ? balance >= pending.RequiredUnits : balance > 0)
            {
                RearmIfRecovered(balance);
                return;
            }
            if (_last is not null && _last.RequiredUnits == pending.RequiredUnits
                && _last.BalanceUnits == balance && _last.Kind == pending.Kind)
            {
                // Unchanged shortages are not useful reminders. Recheck later without another DM.
                _nextAttempt = _time.GetUtcNow() + CheckInterval;
                return;
            }
            var text = await languages.GetAsync(options.Value.AdminUserId, null, timeout.Token);
            var message = pending.Kind == "empty" ? text.Text("balance.empty")
                : text.Text(pending.Kind == "order" ? "balance.order" : "balance.minimum",
                    Usd(balance), Usd(pending.RequiredUnits), Usd(pending.RequiredUnits - balance));
            await bot.SendMessage(options.Value.AdminUserId, message, protectContent: true,
                cancellationToken: timeout.Token);
            _last = new SentNotice(options.Value.AdminUserId, pending.RequiredUnits, balance, pending.Kind, _time.GetUtcNow());
            _nextAttempt = _last.SentAt + CheckInterval;
            _savePending = true;
            await SaveAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Queue(pending);
            var delay = ex is ApiRequestException { ErrorCode: 400 or 403 } ? CheckInterval : RetryInterval;
            if (ex is ApiRequestException { ErrorCode: 429 } limited)
                delay = TimeSpan.FromSeconds(Math.Clamp(limited.Parameters?.RetryAfter ?? 30, 1, 300));
            _nextAttempt = _time.GetUtcNow() + delay;
            LogFailure("notify", ex);
        }
    }

    private void Queue(Notice notice)
    {
        lock (_gate)
        {
            // A rush of rejected checkouts occupies exactly one slot: retain the largest known shortfall.
            if (_pending is null || notice.RequiredUnits - notice.BalanceUnits > _pending.RequiredUnits - _pending.BalanceUnits
                || notice.RequiredUnits - notice.BalanceUnits == _pending.RequiredUnits - _pending.BalanceUnits && notice.Kind == "order")
                _pending = notice;
        }
    }

    private void RearmIfRecovered(long balance)
    {
        if (_last is null || (_last.RequiredUnits > 0 ? balance < _last.RequiredUnits : balance == 0)) return;
        _last = null;
        _nextAttempt = default;
        _savePending = true;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        if (!_savePending || _time.GetUtcNow() < _nextSave) return;
        try
        {
            using var timeout = Timeout(ct);
            // The repository intentionally permits deletion only for temporary support routes.
            // Keep this dedicated key and atomically reset its value after a top-up.
            await state.SetAsync(StateKey, JsonSerializer.Serialize(_last), timeout.Token);
            _savePending = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _nextSave = _time.GetUtcNow() + RetryInterval;
            LogFailure("save", ex);
        }
    }

    private static string Usd(long units) => "$" + (units / 10000m).ToString("0.00##", CultureInfo.InvariantCulture);
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static CancellationTokenSource Timeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }
    private void LogFailure(string operation, Exception ex) => logger.LogWarning(
        "Supplier balance monitor {Operation} failed ({ErrorType})", operation, ex.GetType().Name);

    private sealed record Notice(long RequiredUnits, long BalanceUnits, string Kind);
    private sealed record SentNotice(long AdminId, long RequiredUnits, long BalanceUnits, string Kind, DateTimeOffset SentAt);
}

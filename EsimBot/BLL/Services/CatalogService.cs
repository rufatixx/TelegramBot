using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

/// <summary>One bounded, shared catalogue refresh for the entire application.</summary>
public sealed class CatalogService(IEsimAccessClient provider, TimeProvider? timeProvider = null) : ICatalogService
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallerTimeout = TimeSpan.FromSeconds(8);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private IReadOnlyList<EsimPackage>? _snapshot;
    private DateTimeOffset _freshUntil;
    private DateTimeOffset _retryAfter;
    private ProviderException? _lastFailure;
    private Task<IReadOnlyList<EsimPackage>>? _refresh;

    public Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<IReadOnlyList<EsimPackage>> refresh;
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            if (_snapshot is not null && now < _freshUntil) return Task.FromResult(_snapshot);
            if (_lastFailure is not null && now < _retryAfter)
                return Task.FromException<IReadOnlyList<EsimPackage>>(_lastFailure);
            if (_refresh is null)
            {
                var completion = new TaskCompletionSource<IReadOnlyList<EsimPackage>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _refresh = completion.Task;
                refresh = completion.Task;
                // Observe errors even if every waiting user leaves before the shared refresh finishes.
                _ = _refresh.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _ = RefreshAsync(completion);
            }
            else refresh = _refresh;
        }
        return WaitForRefreshAsync(refresh, ct);
    }

    private static async Task<IReadOnlyList<EsimPackage>> WaitForRefreshAsync(Task<IReadOnlyList<EsimPackage>> refresh, CancellationToken ct)
    {
        try { return await refresh.WaitAsync(CallerTimeout, ct); }
        catch (TimeoutException) { throw new ProviderException("catalog_busy", true); }
    }

    private async Task RefreshAsync(TaskCompletionSource<IReadOnlyList<EsimPackage>> completion)
    {
        using var timeout = new CancellationTokenSource(RefreshTimeout);
        try
        {
            // User cancellation only cancels that user's wait, never everybody else's refresh.
            var packages = await provider.GetPackagesAsync(timeout.Token).WaitAsync(timeout.Token);
            IReadOnlyList<EsimPackage> snapshot = Array.AsReadOnly(packages.Select(package => package with
            {
                Countries = Array.AsReadOnly(package.Countries.ToArray())
            }).ToArray());
            lock (_sync)
            {
                _snapshot = snapshot;
                _freshUntil = _clock.GetUtcNow() + FreshFor;
                _lastFailure = null;
                _refresh = null;
                completion.TrySetResult(snapshot);
            }
        }
        catch (Exception ex)
        {
            var failure = ex as ProviderException ?? new ProviderException(
                ex is OperationCanceledException ? "catalog_timeout" : "catalog_unavailable", true);
            lock (_sync)
            {
                _lastFailure = failure;
                _retryAfter = _clock.GetUtcNow() + RetryAfterFailure;
                _refresh = null;
                completion.TrySetException(failure);
            }
        }
    }
}

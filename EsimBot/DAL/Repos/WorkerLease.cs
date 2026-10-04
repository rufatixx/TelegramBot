using Dapper;
using MySqlConnector;

namespace EsimBot.DAL.Repos;

/// <summary>Owns one nonpooled database session and its exclusive worker lock.</summary>
public sealed class WorkerLease : IWorkerLease
{
    private readonly MySqlConnection _connection;
    private readonly string _lockName;
    private int _disposed;

    private WorkerLease(MySqlConnection connection, string lockName)
    {
        _connection = connection;
        _lockName = lockName;
    }

    internal static async Task<WorkerLease> AcquireAsync(MySqlConnection connection, string lockName, CancellationToken ct)
    {
        var acquired = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT GET_LOCK(@Name,0)", new { Name = lockName }, commandTimeout: 5, cancellationToken: ct));
        if (acquired != 1) throw new InvalidOperationException("Another eSIM worker is already running or the worker lock is unavailable.");
        return new WorkerLease(connection, lockName);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        // Check ownership, not just reachability: a live connection without the lock is not a valid lease.
        return await _connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT IS_USED_LOCK(@Name)=CONNECTION_ID()", new { Name = _lockName },
            commandTimeout: 5, cancellationToken: ct)) == true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _connection.ExecuteAsync(new CommandDefinition("DO RELEASE_LOCK(@Name)",
                new { Name = _lockName }, commandTimeout: 5, cancellationToken: timeout.Token));
        }
        catch { /* A lost/closed session also releases its MySQL advisory lock. */ }
        finally { await _connection.DisposeAsync(); }
    }
}

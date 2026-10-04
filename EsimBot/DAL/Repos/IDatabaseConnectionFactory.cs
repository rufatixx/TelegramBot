using MySqlConnector;

namespace EsimBot.DAL.Repos;

public interface IDatabaseConnectionFactory
{
    Task<MySqlConnection> OpenAsync(CancellationToken ct);
    Task<IDatabaseSession> OpenSessionAsync(CancellationToken ct);
    Task<IDatabaseSession> BeginTransactionAsync(CancellationToken ct);
    Task<IWorkerLease> AcquireWorkerLeaseAsync(CancellationToken ct);
}

public interface IDatabaseSession : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

public interface IWorkerLease : IAsyncDisposable
{
    Task<bool> PingAsync(CancellationToken ct);
}

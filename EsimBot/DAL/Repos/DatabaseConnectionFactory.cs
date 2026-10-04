using System.Data;
using Dapper;
using MySqlConnector;

namespace EsimBot.DAL.Repos;

public sealed class DatabaseConnectionFactory : IDatabaseConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _environment;

    public DatabaseConnectionFactory(IConfiguration configuration)
    {
        var isTest = DatabaseEnvironment.IsTest(configuration);
        _environment = isTest ? "test" : "production";
        _connectionString = DatabaseEnvironment.ResolveConnectionString(configuration, isTest);
    }

    public Task<MySqlConnection> OpenAsync(CancellationToken ct) => OpenConnectionAsync(false, ct);

    public async Task<IDatabaseSession> OpenSessionAsync(CancellationToken ct)
        => new DatabaseSession(await OpenAsync(ct), null);

    public async Task<IDatabaseSession> BeginTransactionAsync(CancellationToken ct)
    {
        var connection = await OpenAsync(ct);
        try { return new DatabaseSession(connection, await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)); }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<IWorkerLease> AcquireWorkerLeaseAsync(CancellationToken ct)
    {
        var connection = await OpenConnectionAsync(true, ct);
        // Keep the established production lock name for zero-overlap deployments;
        // the sandbox uses its own lock and can never block or impersonate production.
        var lockName = _environment == "production" ? "esim_bot:worker" : "esim_bot:worker:test";
        try { return await WorkerLease.AcquireAsync(connection, lockName, ct); }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task<MySqlConnection> OpenConnectionAsync(bool dedicated, CancellationToken ct)
    {
        var connectionString = _connectionString;
        if (dedicated) connectionString = new MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("SET SESSION time_zone='+00:00'", cancellationToken: ct));
            await DatabaseEnvironment.GuardBindingAsync(connection, _environment, ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}

internal sealed class DatabaseSession(MySqlConnection connection, MySqlTransaction? transaction) : IDatabaseSession
{
    internal MySqlConnection Connection { get; } = connection;
    internal MySqlTransaction? Transaction { get; } = transaction;

    internal static DatabaseSession Require(IDatabaseSession session) => session as DatabaseSession
        ?? throw new ArgumentException("The session must be created by DatabaseConnectionFactory.", nameof(session));

    public Task CommitAsync(CancellationToken ct) => Transaction?.CommitAsync(ct)
        ?? throw new InvalidOperationException("A read-only session has no transaction to commit.");

    public async ValueTask DisposeAsync()
    {
        try { if (Transaction is not null) await Transaction.DisposeAsync(); }
        finally { await Connection.DisposeAsync(); }
    }
}

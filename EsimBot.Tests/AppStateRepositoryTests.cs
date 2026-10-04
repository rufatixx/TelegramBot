using EsimBot.DAL.Repos;
using MySqlConnector;

namespace EsimBot.Tests;

public sealed class AppStateRepositoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("schema_version")]
    [InlineData("payment_environment")]
    [InlineData("telegram_offset")]
    [InlineData("language:123")]
    [InlineData("support")]
    [InlineData("support:mode:")]
    [InlineData("support:route:")]
    [InlineData("support:modeevil:123")]
    [InlineData("support:routeevil:123")]
    [InlineData("Support:mode:123")]
    [InlineData(" support:mode:123")]
    [InlineData("x:support:route:123")]
    public async Task Deletion_rejects_system_keys_and_similar_namespaces_before_opening_a_connection(string? key)
    {
        var state = new AppStateRepository(new NoNetworkFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.DeleteAsync(key!, default));
    }

    [Theory]
    [InlineData("support:mode:123")]
    [InlineData("support:route:123:456")]
    public async Task Only_a_nonempty_support_key_can_reach_the_database(string key)
    {
        var state = new AppStateRepository(new NoNetworkFactory());
        await Assert.ThrowsAsync<NotSupportedException>(() => state.DeleteAsync(key, default));
    }

    private sealed class NoNetworkFactory : IDatabaseConnectionFactory
    {
        public Task<MySqlConnection> OpenAsync(CancellationToken ct) => throw new NotSupportedException("Network must not be used by this test.");
        public Task<IDatabaseSession> OpenSessionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IDatabaseSession> BeginTransactionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IWorkerLease> AcquireWorkerLeaseAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}

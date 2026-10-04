namespace EsimBot.DAL.Repos;

public interface IAppStateRepository
{
    Task<long> CountUsersAsync(CancellationToken ct) => throw new NotSupportedException();
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task DeleteExpiredSupportStateAsync(long nowUnixSeconds, CancellationToken ct);
    Task VerifyAsync(CancellationToken ct);
    Task EnsureEnvironmentAsync(string environment, CancellationToken ct);
}

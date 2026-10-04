using Dapper;

namespace EsimBot.DAL.Repos;

public sealed class AppStateRepository(IDatabaseConnectionFactory database) : IAppStateRepository
{
    public async Task<long> CountUsersAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await db.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM app_state WHERE `key` LIKE 'language:%'", cancellationToken: ct));
    }

    public async Task EnsureEnvironmentAsync(string environment, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await DatabaseEnvironment.GuardBindingAsync(db, environment, ct);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        return await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT `value` FROM app_state WHERE `key`=@Key", new { Key = key }, cancellationToken: ct));
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        if (key == DatabaseEnvironment.MarkerKey)
            throw new InvalidOperationException("The payment environment marker cannot be overwritten.");
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition(
            "INSERT INTO app_state (`key`,`value`) VALUES (@Key,@Value) ON DUPLICATE KEY UPDATE `value`=@Value",
            new { Key = key, Value = value }, cancellationToken: ct));
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key)
            || !(key.StartsWith("support:mode:", StringComparison.Ordinal) && key.Length > "support:mode:".Length
                || key.StartsWith("support:route:", StringComparison.Ordinal) && key.Length > "support:route:".Length))
            throw new InvalidOperationException("Only individual temporary support keys can be deleted.");
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("DELETE FROM app_state WHERE `key`=@Key",
            new { Key = key }, cancellationToken: ct));
    }

    public async Task DeleteExpiredSupportStateAsync(long nowUnixSeconds, CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("""
            DELETE FROM app_state
            WHERE (`key` LIKE 'support:mode:_%' OR `key` LIKE 'support:route:_%')
              AND CASE WHEN JSON_VALID(`value`) THEN
                    CASE WHEN JSON_TYPE(JSON_EXTRACT(`value`, '$.expiresAt')) = 'INTEGER'
                         THEN CAST(JSON_UNQUOTE(JSON_EXTRACT(`value`, '$.expiresAt')) AS DECIMAL(20,0)) <= @Now
                         ELSE FALSE END
                  ELSE FALSE END
            """, new { Now = nowUnixSeconds }, cancellationToken: ct));
        // Nested CASE guards JSON parsing and numeric conversion even if SQL predicates are reordered.
    }

    public async Task VerifyAsync(CancellationToken ct)
    {
        if (await GetAsync("schema_version", ct) != "1") throw new InvalidOperationException("Unsupported or missing database schema.");
        await using var db = await database.OpenAsync(ct);
        var count = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ('orders','payments','esims','app_state')",
            cancellationToken: ct));
        if (count != 4) throw new InvalidOperationException("Incomplete database schema.");
    }
}

using Dapper;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Configuration;

namespace EsimBot.Tests;

public sealed class SupportStateIntegrationTests
{
    [DatabaseFact]
    public async Task Cleanup_removes_only_expired_integer_support_metadata_and_preserves_every_other_key()
    {
        var path = Path.GetFullPath(Environment.GetEnvironmentVariable("ESIMBOT_INTEGRATION_CONFIG")
            ?? throw new InvalidOperationException("Explicit integration configuration is required."));
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(path)!).AddJsonFile(Path.GetFileName(path), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PaymentTesting:Enabled"] = "true" }).Build();
        var database = new DatabaseConnectionFactory(configuration);
        var state = new AppStateRepository(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var suffix = Guid.NewGuid().ToString("N");
        var fixtures = new Dictionary<string, string>
        {
            [$"support:mode:{suffix}:expired"] = """{"expiresAt":-1}""",
            [$"support:route:{suffix}:boundary"] = """{"expiresAt":0}""",
            [$"support:mode:{suffix}:future"] = """{"expiresAt":1}""",
            [$"support:route:{suffix}:future"] = """{"expiresAt":9223372036854775807}""",
            [$"support:mode:{suffix}:malformed"] = "{not-json",
            [$"support:route:{suffix}:missing"] = "{}",
            [$"support:mode:{suffix}:text"] = """{"expiresAt":"-1"}""",
            [$"support:route:{suffix}:fraction"] = """{"expiresAt":-0.5}""",
            [$"support:mode:{suffix}:decimal"] = """{"expiresAt":0.0}""",
            [$"support:route:{suffix}:null"] = """{"expiresAt":null}""",
            [$"support:mode:{suffix}:bool"] = """{"expiresAt":false}""",
            [$"support:route:{suffix}:array"] = """{"expiresAt":[-1]}""",
            [$"support:mode:{suffix}:nested"] = """{"metadata":{"expiresAt":-1}}""",
            [$"support:route:{suffix}:scalar"] = "-1",
            [$"support:modeevil:{suffix}"] = """{"expiresAt":-1}""",
            [$"support:routeevil:{suffix}"] = """{"expiresAt":-1}""",
            [$"Support:mode:{suffix}"] = """{"expiresAt":-1}""",
            [$"language:integration:{suffix}"] = """{"expiresAt":-1}""",
            [$"support:mode:{suffix}:explicit"] = """{"expiresAt":9223372036854775807}"""
        };
        var keys = fixtures.Keys.ToArray();
        await using var db = await database.OpenAsync(ct);
        var original = (await db.QueryAsync<StateRow>(new CommandDefinition(
            "SELECT `key` AS `Key`,`value` AS `Value` FROM app_state", cancellationToken: ct))).ToArray();
        // The cleanup method is global by design. Refuse to exercise it if this sandbox already
        // contains support state, so the opt-in test never deletes somebody else's metadata.
        Assert.DoesNotContain(original, row => row.Key.StartsWith("support:mode:", StringComparison.Ordinal)
            || row.Key.StartsWith("support:route:", StringComparison.Ordinal));
        try
        {
            await db.ExecuteAsync(new CommandDefinition("INSERT INTO app_state (`key`,`value`) VALUES (@Key,@Value)",
                fixtures.Select(pair => new { pair.Key, pair.Value }), cancellationToken: ct));
            await state.DeleteExpiredSupportStateAsync(0, ct);
            var afterCleanup = (await db.QueryAsync<StateRow>(new CommandDefinition(
                "SELECT `key` AS `Key`,`value` AS `Value` FROM app_state WHERE `key` IN @Keys",
                new { Keys = keys }, cancellationToken: ct))).ToDictionary(row => row.Key, row => row.Value);
            var removed = new[] { $"support:mode:{suffix}:expired", $"support:route:{suffix}:boundary" };
            Assert.Equal(fixtures.Count - removed.Length, afterCleanup.Count);
            foreach (var pair in fixtures)
            {
                if (removed.Contains(pair.Key)) Assert.False(afterCleanup.ContainsKey(pair.Key));
                else Assert.True(afterCleanup.TryGetValue(pair.Key, out var value) && value == pair.Value,
                    "A non-expired, malformed, noninteger, or unrelated value was changed.");
            }

            var explicitKey = $"support:mode:{suffix}:explicit";
            await state.DeleteAsync(explicitKey, ct);
            await state.DeleteAsync(explicitKey, ct); // Missing state is harmless and idempotent.
            Assert.Null(await state.GetAsync(explicitKey, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => state.DeleteAsync(DatabaseEnvironment.MarkerKey, ct));
            foreach (var prior in original)
                Assert.True(await state.GetAsync(prior.Key, ct) == prior.Value, "Pre-existing application state was changed.");
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var cleanup = await database.OpenAsync(cleanupTimeout.Token);
            await cleanup.ExecuteAsync(new CommandDefinition("DELETE FROM app_state WHERE `key` IN @Keys",
                new { Keys = keys }, cancellationToken: cleanupTimeout.Token));
            Assert.Equal(0, await cleanup.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM app_state WHERE `key` IN @Keys", new { Keys = keys }, cancellationToken: cleanupTimeout.Token)));
        }
    }

    private sealed record StateRow(string Key, string Value);
}

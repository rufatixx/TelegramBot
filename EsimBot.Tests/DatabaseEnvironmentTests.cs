using System.Text.Json;
using System.Text.Json.Nodes;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace EsimBot.Tests;

public sealed class DatabaseEnvironmentTests
{
    private const string Production = "Server=localhost;Database=esim_bot;User ID=esim_bot_app;Password=unit-test-placeholder;SSL Mode=Required";
    private const string Sandbox = "Server=localhost;Database=esim_bot_test;User ID=esim_bot_test_app;Password=unit-test-placeholder;SSL Mode=Required";

    [Fact]
    public void Test_mode_requires_its_own_connection_and_never_falls_back_to_production()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ResolveConnectionString(Config(true, Production, null)));
        var selected = new MySqlConnectionStringBuilder(DatabaseEnvironment.ResolveConnectionString(Config(true, Production, Sandbox)));
        Assert.Equal("esim_bot_test", selected.Database);
        Assert.Equal("esim_bot_test_app", selected.UserID);
        Assert.Equal(MySqlSslMode.Required, selected.SslMode);
        Assert.Equal("test", DatabaseEnvironment.Name(Config(true)));
    }

    [Theory]
    [InlineData("esim_bot", "esim_bot_test_app", "Required")]
    [InlineData("ESIM_BOT_TEST", "esim_bot_test_app", "Required")]
    [InlineData("another_database", "esim_bot_test_app", "Required")]
    [InlineData("esim_bot_test", "root", "Required")]
    [InlineData("esim_bot_test", "esim_bot_app", "Required")]
    [InlineData("esim_bot_test", "esim_bot_test_app", "Preferred")]
    [InlineData("esim_bot_test", "esim_bot_test_app", "None")]
    public void Test_mode_rejects_wrong_database_account_or_optional_TLS(string name, string user, string ssl)
    {
        var connection = $"Server=localhost;Database={name};User ID={user};SSL Mode={ssl}";
        Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ResolveConnectionString(Config(true, Production, connection)));
    }

    [Theory]
    [InlineData("esim_bot_test", "esim_bot_app")]
    [InlineData("ESIM_BOT_TEST", "esim_bot_app")]
    [InlineData("esim_bot", "esim_bot_test_app")]
    [InlineData("esim_bot", "ESIM_BOT_TEST_APP")]
    public void Production_cannot_reuse_the_test_database_or_identity(string name, string user)
    {
        var connection = $"Server=localhost;Database={name};User ID={user};SSL Mode=Required";
        Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ResolveConnectionString(Config(false, connection, Sandbox)));
    }

    [Fact]
    public void Production_ignores_unused_test_settings_and_requires_an_explicit_database()
    {
        var configuration = Config(false, Production, "not-a-valid-connection");
        Assert.Equal("production", DatabaseEnvironment.Name(configuration));
        Assert.Equal("esim_bot", new MySqlConnectionStringBuilder(DatabaseEnvironment.ResolveConnectionString(configuration)).Database);
        Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ResolveConnectionString(Config(false, "Server=localhost", Sandbox)));
    }

    [Theory]
    [InlineData("production", "test")]
    [InlineData("test", "production")]
    [InlineData("production", "unknown")]
    [InlineData("test", "TEST")]
    public void A_persisted_marker_cannot_be_rebound_to_another_environment(string expected, string stored)
        => Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ValidateBinding(expected, stored, false));

    [Fact]
    public void Only_clean_legacy_production_can_be_adopted_and_tests_require_explicit_setup()
    {
        DatabaseEnvironment.ValidateBinding("production", null, false);
        DatabaseEnvironment.ValidateBinding("production", "production", false);
        DatabaseEnvironment.ValidateBinding("test", "test", false);
        Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ValidateBinding("production", null, true));
        Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ValidateBinding("test", null, false));
        Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ValidateBinding("test", null, true));
        Assert.Throws<ArgumentException>(() => DatabaseEnvironment.ValidateBinding("unknown", "unknown", false));
    }

    [Fact]
    public async Task The_generic_key_value_setter_cannot_overwrite_an_environment_marker()
    {
        var state = new AppStateRepository(new NoNetworkFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.SetAsync(DatabaseEnvironment.MarkerKey, "test", default));
    }

    [Fact]
    public void Merging_test_credentials_preserves_all_existing_configuration_and_private_permissions()
    {
        using var files = new SettingsFiles();
        var backup = Path.Combine(files.Directory, "appsettings.Local.json");
        var originalBackup = "{\"legacy_backup\":\"preserve-without-reading\"}";
        File.WriteAllText(backup, originalBackup);
        var original = JsonNode.Parse(File.ReadAllText(files.Target))!.AsObject();
        DatabaseSetup.MergeTestSettings(files.Directory, files.Secrets);
        var merged = JsonNode.Parse(File.ReadAllText(files.Target))!.AsObject();
        var selected = new MySqlConnectionStringBuilder(merged["ConnectionStrings"]!["EsimBotTest"]!.GetValue<string>());
        Assert.Equal("esim_bot_test", selected.Database);
        merged["ConnectionStrings"]!.AsObject().Remove("EsimBotTest");
        Assert.True(JsonNode.DeepEquals(original, merged));
        Assert.Equal(originalBackup, File.ReadAllText(backup));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.Target));
        Assert.Empty(System.IO.Directory.EnumerateFiles(files.Directory, "*.tmp"));
    }

    [Fact]
    public void An_existing_test_connection_is_not_replaced()
    {
        using var files = new SettingsFiles();
        DatabaseSetup.MergeTestSettings(files.Directory, files.Secrets);
        var original = File.ReadAllText(files.Target);
        Assert.Throws<InvalidOperationException>(() => DatabaseSetup.MergeTestSettings(files.Directory, files.Secrets));
        Assert.Equal(original, File.ReadAllText(files.Target));
    }

    [Fact]
    public void A_production_connection_in_test_secret_file_is_rejected_without_touching_settings()
    {
        using var files = new SettingsFiles(Production);
        var original = File.ReadAllText(files.Target);
        Assert.Throws<InvalidOperationException>(() => DatabaseSetup.MergeTestSettings(files.Directory, files.Secrets));
        Assert.Equal(original, File.ReadAllText(files.Target));
    }

    private static IConfiguration Config(bool enabled, string? production = Production, string? sandbox = Sandbox)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PaymentTesting:Enabled"] = enabled.ToString(),
            ["ConnectionStrings:EsimBot"] = production,
            ["ConnectionStrings:EsimBotTest"] = sandbox
        }).Build();

    private sealed class NoNetworkFactory : IDatabaseConnectionFactory
    {
        public Task<MySqlConnection> OpenAsync(CancellationToken ct) => throw new NotSupportedException("Network must not be used by this test.");
        public Task<IDatabaseSession> OpenSessionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IDatabaseSession> BeginTransactionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IWorkerLease> AcquireWorkerLeaseAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class SettingsFiles : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("esimbot-settings-test-").FullName;
        public string Target => Path.Combine(Directory, "appsettings.json");
        public string Secrets => Path.Combine(Directory, "test-secrets.json");

        public SettingsFiles(string testConnection = Sandbox)
        {
            File.WriteAllText(Target, JsonSerializer.Serialize(new
            {
                ConnectionStrings = new { EsimBot = Production }, Storage = new { EncryptionKey = "preserve-this-key" },
                PaymentTesting = new { Enabled = false }, Other = new { Items = new[] { "one", "two" } }
            }));
            File.WriteAllText(Secrets, JsonSerializer.Serialize(new Dictionary<string, string>
            { ["ConnectionStrings:EsimBotTest"] = testConnection }));
        }

        public void Dispose()
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory)) File.Delete(file);
            System.IO.Directory.Delete(Directory);
        }
    }
}

using Dapper;
using MySqlConnector;

namespace EsimBot.DAL.Repos;

/// <summary>Fail-closed database selection and permanent payment-environment binding.</summary>
public static class DatabaseEnvironment
{
    public const string TestDatabaseName = "esim_bot_test";
    public const string TestAccountName = "esim_bot_test_app";
    public const string TestPackagePrefix = "__payment_test";
    public const string MarkerKey = "payment_environment";

    public static bool IsTest(IConfiguration configuration) => configuration.GetValue<bool>("PaymentTesting:Enabled");
    public static string Name(IConfiguration configuration) => IsTest(configuration) ? "test" : "production";

    public static string ResolveConnectionString(IConfiguration configuration)
        => ResolveConnectionString(configuration, IsTest(configuration));

    internal static string ResolveConnectionString(IConfiguration configuration, bool isTest)
    {
        var key = isTest ? "EsimBotTest" : "EsimBot";
        var connectionString = configuration.GetConnectionString(key);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"ConnectionStrings:{key} is required for the selected payment environment.");
        MySqlConnectionStringBuilder settings;
        try { settings = new MySqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException($"ConnectionStrings:{key} is invalid."); }
        if (string.IsNullOrWhiteSpace(settings.Database))
            throw new InvalidOperationException("An explicit database name is required.");
        if (isTest)
        {
            if (settings.Database != TestDatabaseName || settings.UserID != TestAccountName)
                throw new InvalidOperationException("Payment tests require the dedicated esim_bot_test database and esim_bot_test_app account.");
            if (settings.SslMode is not (MySqlSslMode.Required or MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull))
                throw new InvalidOperationException("The payment-test database connection must require TLS.");
        }
        else if (settings.Database.Equals(TestDatabaseName, StringComparison.OrdinalIgnoreCase)
                 || settings.UserID.Equals(TestAccountName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A production payment environment cannot use the test database or test account.");
        return settings.ConnectionString;
    }

    /// <summary>Called before any connection can be used for orders, polling or payment processing.</summary>
    public static async Task GuardBindingAsync(MySqlConnection connection, string environment, CancellationToken ct)
    {
        ValidateExpectedEnvironment(environment);
        var bound = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT `value` FROM app_state WHERE `key`=@Key", new { Key = MarkerKey }, cancellationToken: ct));
        if (bound is not null)
        {
            ValidateBinding(environment, bound, false);
            return;
        }
        // A test database must be initialized explicitly by test setup; never adopt an unmarked copy.
        if (environment == "test") ValidateBinding(environment, null, false);
        var hasTestOrders = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM orders WHERE LEFT(package_code,@PrefixLength)=@Prefix LIMIT 1)
            """, new { Prefix = TestPackagePrefix, PrefixLength = TestPackagePrefix.Length }, cancellationToken: ct));
        ValidateBinding(environment, null, hasTestOrders);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO app_state (`key`,`value`) VALUES (@Key,@Environment)",
            new { Key = MarkerKey, Environment = environment }, cancellationToken: ct));
        bound = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT `value` FROM app_state WHERE `key`=@Key", new { Key = MarkerKey }, cancellationToken: ct));
        ValidateBinding(environment, bound, false);
    }

    public static void ValidateBinding(string expected, string? stored, bool hasReservedTestOrders)
    {
        ValidateExpectedEnvironment(expected);
        if (stored is not null)
        {
            if (stored != expected) throw new InvalidOperationException("The database belongs to a different payment environment.");
            return;
        }
        if (expected == "test")
            throw new InvalidOperationException("The test database is unmarked. Initialize a separate test database before using payment tests.");
        if (hasReservedTestOrders)
            throw new InvalidOperationException("An unmarked database contains reserved payment-test orders and cannot be adopted for production.");
    }

    private static void ValidateExpectedEnvironment(string environment)
    {
        if (environment is not ("test" or "production")) throw new ArgumentException("Unknown payment environment.", nameof(environment));
    }
}

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;

namespace EsimBot.DAL.Repos;

public static class DatabaseSetup
{
    public static string SecretsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EsimBot", "secrets.json");
    public static string TestSecretsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EsimBot", "test-secrets.json");

    public static void MergeTestSettings(string directory) => MergeTestSettings(directory, TestSecretsPath);

    public static void MergeTestSettings(string directory, string testSecretsPath)
    {
        var target = Path.Combine(Path.GetFullPath(directory), "appsettings.json");
        if (new FileInfo(target).LinkTarget is not null)
            throw new InvalidOperationException("appsettings.json is a symbolic link; refusing to replace it.");
        var original = File.ReadAllText(target);
        var settings = JsonNode.Parse(original) as JsonObject
            ?? throw new InvalidOperationException("appsettings.json must contain a JSON object.");
        var secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(testSecretsPath))
            ?? throw new InvalidOperationException("Test database credentials are missing.");
        if (!secrets.TryGetValue("ConnectionStrings:EsimBotTest", out var testConnection))
            throw new InvalidOperationException("The test connection string is missing.");
        var validation = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PaymentTesting:Enabled"] = "true", ["ConnectionStrings:EsimBotTest"] = testConnection
        }).Build();
        testConnection = DatabaseEnvironment.ResolveConnectionString(validation);
        if (settings["ConnectionStrings"] is null) settings["ConnectionStrings"] = new JsonObject();
        var connections = settings["ConnectionStrings"] as JsonObject
            ?? throw new InvalidOperationException("ConnectionStrings must be a JSON object.");
        if (connections["EsimBotTest"] is { } existing)
        {
            if (existing is not JsonValue value || !value.TryGetValue<string>(out var configured)
                || !string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException("EsimBotTest is already configured; refusing to replace it.");
        }
        connections["EsimBotTest"] = testConnection;

        var temporary = target + ".test-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, PrivateFileOptions()))
                JsonSerializer.Serialize(file, settings, new JsonSerializerOptions { WriteIndented = true });
            if (File.ReadAllText(target) != original)
                throw new InvalidOperationException("appsettings.json changed during the merge; no settings were replaced.");
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine("Added only ConnectionStrings:EsimBotTest to appsettings.json. Existing credentials, encryption key and payment mode were preserved.");
    }

    public static async Task RunTestAsync(string host, CancellationToken ct)
    {
        if (File.Exists(TestSecretsPath))
            throw new InvalidOperationException("Test database credentials already exist; refusing to overwrite them.");
        Console.Write("MySQL root password (hidden): ");
        var password = ReadSecret();
        var admin = new MySqlConnectionStringBuilder
        {
            Server = host, Port = 3306, UserID = "root", Password = password,
            SslMode = MySqlSslMode.Required, ConnectionTimeout = 10, DefaultCommandTimeout = 30, Pooling = false
        };
        await using var db = new MySqlConnection(admin.ConnectionString);
        await db.OpenAsync(ct);
        await using (var check = new MySqlCommand("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='esim_bot_test'", db))
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("esim_bot_test already exists; refusing to overwrite it.");
        await using (var check = new MySqlCommand("SELECT COUNT(*) FROM mysql.user WHERE User='esim_bot_test_app'", db))
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("esim_bot_test_app already exists; refusing to overwrite it.");
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var application = new MySqlConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = DatabaseEnvironment.TestAccountName, Password = appPassword,
            Database = DatabaseEnvironment.TestDatabaseName, Pooling = true, MaximumPoolSize = 20
        };
        var directory = Path.GetDirectoryName(TestSecretsPath)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (var file = new FileStream(TestSecretsPath, PrivateFileOptions()))
            await JsonSerializer.SerializeAsync(file, new Dictionary<string, string>
            {
                ["ConnectionStrings:EsimBotTest"] = application.ConnectionString
            }, new JsonSerializerOptions { WriteIndented = true }, ct);

        await ExecuteAsync(db, "CREATE DATABASE esim_bot_test CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci", ct);
        await db.ChangeDatabaseAsync(DatabaseEnvironment.TestDatabaseName, ct);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EsimBot.DAL.schema.sql")!;
        using var reader = new StreamReader(stream);
        await ExecuteAsync(db, await reader.ReadToEndAsync(ct), ct);
        await ExecuteAsync(db, "INSERT INTO app_state (`key`,`value`) VALUES ('payment_environment','test')", ct);
        // The generated password is hexadecimal; neither the root password nor application credentials are printed.
        await ExecuteAsync(db, $"CREATE USER 'esim_bot_test_app'@'%' IDENTIFIED BY '{appPassword}' REQUIRE SSL", ct);
        await ExecuteAsync(db, "GRANT SELECT, INSERT, UPDATE, DELETE ON esim_bot_test.* TO 'esim_bot_test_app'@'%'", ct);
        await using var applicationDb = new MySqlConnection(application.ConnectionString);
        await applicationDb.OpenAsync(ct);
        await DatabaseEnvironment.GuardBindingAsync(applicationDb, "test", ct);
        await using var count = new MySqlCommand("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='esim_bot_test'", applicationDb);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) != 4)
            throw new InvalidOperationException("Test database schema is incomplete.");
        Console.WriteLine("Created esim_bot_test with 4 tables, test environment marker, TLS and a dedicated data-only account.");
        Console.WriteLine($"Test database credentials saved separately at: {TestSecretsPath}");
        Console.WriteLine("Production database, production account, encryption key and local appsettings were not modified.");
    }

    public static void ExportLocalSettings(string directory)
    {
        var target = Path.Combine(Path.GetFullPath(directory), "appsettings.Local.json");
        if (File.Exists(target)) throw new InvalidOperationException("appsettings.Local.json exists; refusing to replace local settings.");
        var secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SecretsPath))
            ?? throw new InvalidOperationException("Database setup secrets are missing.");
        var template = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "appsettings.json")))!;
        template["ConnectionStrings"]!["EsimBot"] = secrets["ConnectionStrings:EsimBot"];
        template["Storage"]!["EncryptionKey"] = secrets["Storage:EncryptionKey"];
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(target, options);
        JsonSerializer.Serialize(file, template, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Local appsettings created: {target}. Secret values were not printed.");
    }

    public static async Task RunAsync(string host, CancellationToken ct)
    {
        if (File.Exists(SecretsPath))
            throw new InvalidOperationException("Local secrets already exist. Refusing to replace the encryption key or database account.");
        Console.Write("MySQL root password (hidden): ");
        var password = ReadSecret();
        var builder = new MySqlConnectionStringBuilder
        {
            Server = host, Port = 3306, UserID = "root", Password = password,
            SslMode = MySqlSslMode.Required, ConnectionTimeout = 10,
            DefaultCommandTimeout = 30, Pooling = false
        };
        await using var db = new MySqlConnection(builder.ConnectionString);
        await db.OpenAsync(ct);
        await using (var check = new MySqlCommand("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='esim_bot'", db))
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("esim_bot already exists; refusing to initialize or overwrite an existing database.");
        await using (var check = new MySqlCommand("SELECT COUNT(*) FROM mysql.user WHERE User='esim_bot_app'", db))
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("esim_bot_app already exists; refusing to overwrite an existing account.");

        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        // Save generated keys before DDL: a interrupted setup must never lose the activation encryption key.
        var appBuilder = new MySqlConnectionStringBuilder(builder.ConnectionString)
        {
            UserID = "esim_bot_app", Password = appPassword, Database = "esim_bot", Pooling = true,
            MinimumPoolSize = 0, MaximumPoolSize = 20
        };
        var directory = Path.GetDirectoryName(SecretsPath)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(SecretsPath, fileOptions))
        {
            await JsonSerializer.SerializeAsync(file, new Dictionary<string, string>
            {
                ["ConnectionStrings:EsimBot"] = appBuilder.ConnectionString,
                ["Storage:EncryptionKey"] = encryptionKey
            }, new JsonSerializerOptions { WriteIndented = true }, ct);
        }

        await ExecuteAsync(db, "CREATE DATABASE esim_bot CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci", ct);
        await db.ChangeDatabaseAsync("esim_bot", ct);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EsimBot.DAL.schema.sql")!;
        using var reader = new StreamReader(stream);
        await ExecuteAsync(db, await reader.ReadToEndAsync(ct), ct);
        await ExecuteAsync(db, "INSERT INTO app_state (`key`,`value`) VALUES ('payment_environment','production')", ct);
        // Password is generated as hexadecimal, not user-controlled SQL text. Never printed or written to source.
        await ExecuteAsync(db, $"CREATE USER 'esim_bot_app'@'%' IDENTIFIED BY '{appPassword}' REQUIRE SSL", ct);
        await ExecuteAsync(db, "GRANT SELECT, INSERT, UPDATE, DELETE ON esim_bot.* TO 'esim_bot_app'@'%'", ct);
        await using var appDb = new MySqlConnection(appBuilder.ConnectionString);
        await appDb.OpenAsync(ct);
        await using var count = new MySqlCommand("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='esim_bot'", appDb);
        Console.WriteLine($"Created esim_bot. Tables: {await count.ExecuteScalarAsync(ct)}. Application login verified over TLS.");
        Console.WriteLine($"Generated application credentials and encryption key stored at: {SecretsPath}");
        Console.WriteLine("The root password was not stored. Keep a secure backup of the encryption key.");
    }

    private static async Task ExecuteAsync(MySqlConnection db, string sql, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static FileStreamOptions PrivateFileOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static string ReadSecret()
    {
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}

using EsimBot.BLL.Services;
using EsimBot.DAL.Repos;
using MySqlConnector;

namespace EsimBot.BLL.DTO;

/// <summary>Public status contains only flags, never configured values or credentials.</summary>
public sealed record StartupConfiguration(bool DatabaseConfigured, bool StorageConfigured, bool TelegramConfigured,
    bool ModeConfigured, bool WebhookConfigured, bool ProviderConfigured, bool SalesConfigured, bool AdministratorConfigured,
    bool TestMode = false, bool TestPaymentsConfigured = false)
{
    public bool CanStartWorker => DatabaseConfigured && StorageConfigured && TelegramConfigured && ModeConfigured && WebhookConfigured;
    public bool CanSell => !TestMode && CanStartWorker && ProviderConfigured && SalesConfigured && AdministratorConfigured;
    public bool CanTestPayments => TestMode && CanStartWorker && TestPaymentsConfigured;
    public string PaymentEnvironment => TestMode ? "test" : "production";

    public static StartupConfiguration Inspect(IConfiguration configuration)
    {
        var databaseConfigured = false;
        try
        {
            var connection = new MySqlConnectionStringBuilder(DatabaseEnvironment.ResolveConnectionString(configuration));
            databaseConfigured = !string.IsNullOrWhiteSpace(connection.Server) && !string.IsNullOrWhiteSpace(connection.Database)
                && !string.IsNullOrWhiteSpace(connection.UserID) && !string.IsNullOrWhiteSpace(connection.Password)
                && connection.SslMode is MySqlSslMode.Required or MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        var storageConfigured = false;
        try { storageConfigured = Convert.FromBase64String(configuration["Storage:EncryptionKey"] ?? "").Length == 32; }
        catch (FormatException) { }
        var bot = BotOptions.Resolve(configuration);
        var testing = configuration.GetSection(PaymentTestOptions.SectionName).Get<PaymentTestOptions>() ?? new();
        var sales = configuration.GetSection("Sales").Get<SalesOptions>() ?? new();
        var modeConfigured = bot.Mode is "Polling" or "Webhook";
        var webhookConfigured = bot.Mode != "Webhook" || (WebhookSecurity.IsWebhookSecretValid(bot.WebhookSecret)
            && Uri.TryCreate(bot.WebhookUrl, UriKind.Absolute, out var webhookUrl)
            && webhookUrl.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(webhookUrl.UserInfo)
            && webhookUrl.AbsolutePath == "/api/telegram/webhook" && string.IsNullOrEmpty(webhookUrl.Query));
        return new StartupConfiguration(databaseConfigured, storageConfigured, !string.IsNullOrWhiteSpace(bot.Token),
            modeConfigured, webhookConfigured, !testing.Enabled && !string.IsNullOrWhiteSpace(configuration["Provider:AccessCode"]),
            !testing.Enabled && sales.IsConfigured, bot.AdminUserId > 0, testing.Enabled, testing.IsConfigured);
    }
}

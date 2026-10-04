namespace EsimBot.BLL.DTO;

public sealed class BotOptions
{
    public const string SectionName = "TelegramBot";

    public string Token { get; init; } = string.Empty;
    public long AdminUserId { get; init; }
    public string Mode { get; init; } = "Polling";
    public string WebhookSecret { get; init; } = "";
    public string WebhookUrl { get; init; } = "";
    public string DefaultLanguage { get; init; } = "en";
    public bool TestEnvironment { get; init; }

    public static BotOptions Resolve(IConfiguration configuration)
    {
        var production = configuration.GetSection(SectionName).Get<BotOptions>() ?? new();
        var testing = configuration.GetSection(PaymentTestOptions.SectionName).Get<PaymentTestOptions>() ?? new();
        // The sole switch selects a separate token and disables the production webhook.
        // TelegramBot:TestEnvironment cannot independently redirect production payments.
        return new BotOptions
        {
            Token = testing.Enabled ? testing.BotToken : production.Token,
            AdminUserId = testing.Enabled ? testing.AdminUserId : production.AdminUserId,
            Mode = testing.Enabled ? "Polling" : production.Mode,
            WebhookSecret = testing.Enabled ? "" : production.WebhookSecret,
            WebhookUrl = testing.Enabled ? "" : production.WebhookUrl,
            DefaultLanguage = production.DefaultLanguage,
            TestEnvironment = testing.Enabled
        };
    }
}

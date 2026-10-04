namespace EsimBot.BLL.DTO;

/// <summary>Dedicated Telegram test-server payments. Never represents a purchasable eSIM.</summary>
public sealed class PaymentTestOptions
{
    public const string SectionName = "PaymentTesting";
    public const string PackageCode = "__payment_test__";

    public bool Enabled { get; init; }
    public string BotToken { get; init; } = "";
    public long AdminUserId { get; init; }
    public int InvoiceStars { get; init; } = 1;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BotToken)
        && AdminUserId > 0 && InvoiceStars is >= 1 and <= 100;
}

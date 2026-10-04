namespace EsimBot.BLL.DTO;

public sealed class ProviderOptions
{
    public string AccessCode { get; init; } = "";
    public string SecretKey { get; init; } = "";
}

public sealed class SalesOptions
{
    public const string BuiltInTermsVersion = "builtin-2026-10-04-v1";
    // Telegram's published reward (checked 2026-10-04), less a 10% operating reserve.
    // An estimate, not guaranteed withdrawal proceeds. Topics fees are accounted for separately.
    public const decimal DefaultNetUsdPerStar = 0.013m * 0.90m;

    public bool Enabled { get; init; } = true;
    public decimal NetUsdPerStar { get; init; } = DefaultNetUsdPerStar;
    public decimal MarkupPercent { get; init; } = 10;
    public decimal MinimumProfitUsd { get; init; } = 0.25m;
    public string SupportUrl { get; init; } = "";
    public string TermsUrl { get; init; } = "";
    public string TermsVersion { get; init; } = BuiltInTermsVersion;

    public bool IsConfigured => Enabled && NetUsdPerStar > 0 && NetUsdPerStar <= 1
        && MarkupPercent >= 0 && MinimumProfitUsd >= 0
        && (string.IsNullOrWhiteSpace(SupportUrl) || IsHttps(SupportUrl))
        && (string.IsNullOrWhiteSpace(TermsUrl) || IsHttps(TermsUrl))
        && !string.IsNullOrWhiteSpace(TermsVersion) && TermsVersion.Length <= 64
        // A custom policy must have its own version; never record it as acceptance of built-in terms.
        && (string.IsNullOrWhiteSpace(TermsUrl) || TermsVersion != BuiltInTermsVersion);

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
}

public sealed class StorageOptions
{
    public string EncryptionKey { get; init; } = "";
}

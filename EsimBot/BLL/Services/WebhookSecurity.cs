using System.Security.Cryptography;
using System.Text;

namespace EsimBot.BLL.Services;

public static class WebhookSecurity
{
    public const int MaxWebhookBytes = 256 * 1024;

    public static bool IsWebhookSecretValid(string? value) => value is { Length: >= 1 and <= 256 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static bool VerifyWebhookSecret(string? expected, string? received)
    {
        if (!IsWebhookSecretValid(expected) || !IsWebhookSecretValid(received) || expected!.Length != received!.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(received));
    }
}

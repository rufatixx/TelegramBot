using System.Security.Cryptography;
using System.Text;
using EsimBot.BLL.DTO;
using Microsoft.Extensions.Options;

namespace EsimBot.DAL.Repos;

public sealed class SecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(IOptions<StorageOptions> options)
    {
        try { _key = Convert.FromBase64String(options.Value.EncryptionKey); }
        catch (FormatException) { throw new InvalidOperationException("Storage encryption key must be base64."); }
        if (_key.Length != 32) throw new InvalidOperationException("Storage encryption key must contain 32 bytes.");
    }

    public string Encrypt(string orderId, string activation)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(activation);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(orderId));
        return "v1:" + Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }

    public string Decrypt(string orderId, string encrypted)
    {
        if (!encrypted.StartsWith("v1:", StringComparison.Ordinal)) throw new CryptographicException("Unknown secret format.");
        var bytes = Convert.FromBase64String(encrypted[3..]);
        if (bytes.Length < 28) throw new CryptographicException("Invalid encrypted secret.");
        var plaintext = new byte[bytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(bytes.AsSpan(0,12), bytes.AsSpan(28), bytes.AsSpan(12,16), plaintext, Encoding.UTF8.GetBytes(orderId));
        return Encoding.UTF8.GetString(plaintext);
    }
}

using System.Security.Cryptography;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;

namespace EsimBot.Tests;

public sealed class CoreInvariantTests
{
    [Theory]
    [InlineData(10000, 20, 0.50, 0.013, 116)]
    [InlineData(100000, 20, 0.50, 0.013, 924)]
    [InlineData(10000, 0, 0, 0.01, 100)]
    [InlineData(1, 0, 0, 1, 1)]
    public void Pricing_preserves_profit_and_rounds_up_to_whole_stars(
        long priceUnits, decimal markup, decimal minimumProfit, decimal netPerStar, int expectedStars)
    {
        var pricing = PricingFor(netPerStar, markup, minimumProfit);

        Assert.Equal(expectedStars, pricing.StarsFor(Package(priceUnits)));
    }

    [Theory]
    [InlineData(10000, 20, 0.50, 1.50)]
    [InlineData(100000, 20, 0.50, 12.00)]
    [InlineData(1, 0, 0.25, 0.2501)]
    public void Retail_usd_reference_is_provider_cost_plus_configured_profit(
        long priceUnits, decimal markup, decimal minimumProfit, decimal expectedUsd)
    {
        var pricing = PricingFor(markup: markup, minimumProfit: minimumProfit);

        Assert.Equal(expectedUsd, pricing.RetailUsdFor(Package(priceUnits)));
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("AZN")]
    [InlineData("usd")]
    [InlineData("")]
    public void Pricing_refuses_unsupported_or_ambiguous_currency(string currency)
    {
        Assert.Throws<InvalidOperationException>(() => PricingFor().StarsFor(Package(currency: currency)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Pricing_refuses_nonpositive_provider_cost(long priceUnits)
    {
        Assert.Throws<InvalidOperationException>(() => PricingFor().StarsFor(Package(priceUnits)));
    }

    [Theory]
    [InlineData(0, 20, 0.50)]
    [InlineData(-0.01, 20, 0.50)]
    [InlineData(1.01, 20, 0.50)]
    [InlineData(0.013, -1, 0.50)]
    [InlineData(0.013, 20, -0.01)]
    public void Pricing_refuses_invalid_merchant_settings(decimal netPerStar, decimal markup, decimal minimumProfit)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PricingFor(netPerStar, markup, minimumProfit).StarsFor(Package()));
    }

    [Fact]
    public void Pricing_refuses_integer_overflow_instead_of_undercharging()
    {
        Assert.Throws<OverflowException>(() => PricingFor().StarsFor(Package(long.MaxValue)));
    }

    [Fact]
    public void Activation_roundtrips_without_storing_plaintext_and_uses_a_fresh_nonce()
    {
        var protector = Protector();
        const string orderId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string activation = "LPA:1$smdp.example.com$activation-code-şə-test";

        var first = protector.Encrypt(orderId, activation);
        var second = protector.Encrypt(orderId, activation);

        Assert.StartsWith("v1:", first);
        Assert.DoesNotContain(activation, first);
        Assert.NotEqual(first, second);
        Assert.Equal(activation, protector.Decrypt(orderId, first));
        Assert.Equal(activation, protector.Decrypt(orderId, second));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(28)]
    public void Activation_rejects_tampering_with_nonce_tag_or_ciphertext(int byteToChange)
    {
        var protector = Protector();
        var encrypted = protector.Encrypt("order-a", "LPA:1$smdp.example.com$test");
        var bytes = Convert.FromBase64String(encrypted[3..]);
        bytes[byteToChange] ^= 1;
        var tampered = "v1:" + Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => protector.Decrypt("order-a", tampered));
    }

    [Fact]
    public void Activation_cannot_be_moved_to_another_order()
    {
        var protector = Protector();
        var encrypted = protector.Encrypt("order-a", "LPA:1$smdp.example.com$test");

        Assert.ThrowsAny<CryptographicException>(() => protector.Decrypt("order-b", encrypted));
    }

    [Fact]
    public void Activation_cannot_be_decrypted_with_another_storage_key()
    {
        var encrypted = Protector(1).Encrypt("order-a", "LPA:1$smdp.example.com$test");

        Assert.ThrowsAny<CryptographicException>(() => Protector(2).Decrypt("order-a", encrypted));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AQ==")]
    public void Invalid_storage_keys_fail_at_construction(string key)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SecretProtector(Options.Create(new StorageOptions { EncryptionKey = key })));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(33)]
    public void Storage_requires_exactly_a_256_bit_key(int keyLength)
    {
        var key = Convert.ToBase64String(new byte[keyLength]);

        Assert.Throws<InvalidOperationException>(() =>
            new SecretProtector(Options.Create(new StorageOptions { EncryptionKey = key })));
    }

    [Theory]
    [InlineData("v2:AAAA")]
    [InlineData("v1:")]
    [InlineData("v1:AAAA")]
    public void Unknown_or_truncated_activation_envelopes_are_rejected(string encrypted)
    {
        Assert.ThrowsAny<CryptographicException>(() => Protector().Decrypt("order-a", encrypted));
    }

    private static Pricing PricingFor(decimal netPerStar = 0.013m, decimal markup = 20m, decimal minimumProfit = 0.50m)
        => new(Options.Create(new SalesOptions
        {
            NetUsdPerStar = netPerStar,
            MarkupPercent = markup,
            MinimumProfitUsd = minimumProfit
        }));

    private static EsimPackage Package(long priceUnits = 10000, string currency = "USD")
        => new("package-test", "Test package", priceUnits, currency, 1024L * 1024 * 1024,
            7, "DAY", [new Country("AZ", "Azerbaijan")], "", "", "", "");

    private static SecretProtector Protector(byte seed = 1)
        => new(Options.Create(new StorageOptions
        {
            EncryptionKey = Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray())
        }));
}

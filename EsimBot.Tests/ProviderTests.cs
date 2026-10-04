using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.Extensions.Options;

namespace EsimBot.Tests;

public sealed class ProviderTests
{
    [Fact]
    public async Task CatalogContainsOnlyFixedUsdPlansAndKeepsWholesalePrecision()
    {
        using var handler = new FakeHandler(Json("""
            {"success":true,"obj":{"packageList":[
              {"slug":"TR_1_7","name":"Turkey 1GB","price":12345,"currencyCode":"USD","volume":1073741824,
               "duration":7,"durationUnit":"DAY","location":"TR, GE,TR","dataType":1,"activeType":2},
              {"slug":"Daily","dataType":2},
              {"slug":"OtherCurrency","dataType":1,"currencyCode":"EUR"}
            ]}}
            """));
        var client = Client(handler);
        var package = Assert.Single(await client.GetPackagesAsync(default));
        Assert.Equal("TR_1_7", package.Code);
        Assert.Equal(1.2345m, package.PriceUsd);
        Assert.Equal(["TR", "GE"], package.Countries.Select(c => c.Code).ToArray());
        Assert.Equal("first_connection", package.ActivationPolicy);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.esimaccess.com/api/v1/open/package/list", request.Url);
        Assert.Equal("test-key", request.AccessCode);
        Assert.False(request.Headers.ContainsKey("RT-RequestID"));
        Assert.False(request.Headers.ContainsKey("RT-Timestamp"));
        Assert.False(request.Headers.ContainsKey("RT-Signature"));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("1", body.RootElement.GetProperty("dataType").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidCatalogPriceFailsClosed(long price)
    {
        using var handler = new FakeHandler(Json($$$"""
            {"success":true,"obj":{"packageList":[{"slug":"TR_1_7","name":"Plan","price":{{{price}}},
            "currencyCode":"USD","volume":100,"duration":7,"durationUnit":"DAY","location":"TR","dataType":1,"activeType":2}]}}
            """));
        var error = await Assert.ThrowsAsync<ProviderException>(() => Client(handler).GetPackagesAsync(default));
        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task ReadRetriesTransientFailureWithBoundedAttempts()
    {
        using var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Json("""{"success":true,"obj":{"balance":123456}}"""));
        Assert.Equal(123456, await Client(handler).GetBalanceUnitsAsync(default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PurchaseDoesNotRetryAndReplayKeepsIdAndPrice()
    {
        using var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Json("""{"success":true,"obj":{"orderNo":"B123"}}"""));
        var client = Client(handler);
        var error = await Assert.ThrowsAsync<ProviderException>(() => client.PlaceOrderAsync("stable-order-id", "TR_1_7", 12345, default));
        Assert.True(error.Retryable);
        Assert.Single(handler.Requests);
        Assert.Equal("B123", await client.PlaceOrderAsync("stable-order-id", "TR_1_7", 12345, default));
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("stable-order-id", body.RootElement.GetProperty("transactionId").GetString());
        Assert.Equal(12345, body.RootElement.GetProperty("amount").GetInt64());
        var item = body.RootElement.GetProperty("packageInfoList")[0];
        Assert.Equal(12345, item.GetProperty("price").GetInt64());
        Assert.Equal("TR_1_7", item.GetProperty("slug").GetString());
        Assert.Equal(1, item.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task MalformedPurchaseResponseIsAmbiguousAndNotRetriedLocally()
    {
        using var handler = new FakeHandler(Json("{truncated"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => Client(handler).PlaceOrderAsync("order-id", "TR_1_7", 10000, default));
        Assert.Equal("ambiguous_order_response", error.Code);
        Assert.True(error.Retryable);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnknownOrderNumberDoesNotInventTransactionLookup()
    {
        using var handler = new FakeHandler();
        Assert.Null(await Client(handler).QueryOrderAsync("order-id", null, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PendingAllocationDoesNotFailOrRetryImmediately()
    {
        using var handler = new FakeHandler(Json("""{"success":false,"errorCode":"200010"}"""));
        Assert.Null(await Client(handler).QueryOrderAsync("order-id", "B123", default));
        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("B123", body.RootElement.GetProperty("orderNo").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("pager").GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task AllocatedProfileMustBelongToRequestedOrder()
    {
        using var handler = new FakeHandler(Json("""
            {"success":true,"obj":{"esimList":[{"orderNo":"OTHER","esimTranNo":"E1","iccid":"0012",
            "ac":"LPA:1$smdp.test$activation","esimStatus":"GOT_RESOURCE"}]}}
            """));
        var error = await Assert.ThrowsAsync<ProviderException>(() => Client(handler).QueryOrderAsync("order-id", "B123", default));
        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task UsageQueriesProfileIdAndReadsUtcDateAndBytes()
    {
        using var handler = new FakeHandler(Json("""
            {"success":true,"obj":{"esimList":[{"esimTranNo":"E1","orderUsage":100,"totalVolume":1000,
            "esimStatus":"IN_USE","expiredTime":"2026-10-10T06:20:00+0000"}]}}
            """));
        var usage = await Client(handler).GetUsageAsync("E1", default);
        Assert.Equal(100, usage.UsedBytes);
        Assert.Equal(1000, usage.TotalBytes);
        Assert.Equal(new DateTime(2026, 10, 10, 6, 20, 0, DateTimeKind.Utc), usage.ExpiresAt);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("E1", body.RootElement.GetProperty("esimTranNo").GetString());
        Assert.False(body.RootElement.TryGetProperty("iccid", out _));
    }

    [Fact]
    public async Task SupplierErrorMessagesAndUntrustedCodesDoNotLeakIntoExceptions()
    {
        using var handler = new FakeHandler(Json("""{"success":false,"errorCode":"secret-key","errorMessage":"secret-activation-code"}"""));
        var error = await Assert.ThrowsAsync<ProviderException>(() => Client(handler).GetBalanceUnitsAsync(default));
        Assert.Equal("rejected", error.Code);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingAccessCodeFailsBeforeNetworkCall()
    {
        using var handler = new FakeHandler();
        var client = new EsimAccessClient(new HttpClient(handler), Options.Create(new ProviderOptions()),
            Options.Create(new PaymentTestOptions()));
        Assert.False(client.IsConfigured);
        var error = await Assert.ThrowsAsync<ProviderException>(() => client.GetBalanceUnitsAsync(default));
        Assert.Equal("not_configured", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PaymentTestingBlocksEveryProviderMethodEvenWithLiveCredentials()
    {
        using var handler = new FakeHandler();
        var client = new EsimAccessClient(new HttpClient(handler), Options.Create(new ProviderOptions
            { AccessCode = "configured-live-key", SecretKey = "configured-signing-secret" }),
            Options.Create(new PaymentTestOptions { Enabled = true }));
        Assert.False(client.IsConfigured);
        var calls = new Func<Task>[]
        {
            () => client.GetPackagesAsync(default),
            () => client.GetBalanceUnitsAsync(default),
            () => client.PlaceOrderAsync("stable-order", "TR_1_7", 10000, default),
            () => client.QueryOrderAsync("stable-order", "B123", default),
            () => client.QueryOrderAsync("stable-order", null, default),
            () => client.GetUsageAsync("E1", default)
        };
        foreach (var call in calls)
        {
            var error = await Assert.ThrowsAsync<ProviderException>(call);
            Assert.Equal("payment_test_mode", error.Code);
            Assert.False(error.Retryable);
        }
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HmacSignatureCoversExactUtf8BodyAndNeverTransmitsSecretKey()
    {
        const string secret = "тест-secret-ş-🔐";
        const string transactionId = "заказ-ş-🙂";
        const string packageCode = "test-ş-\"quoted\"";
        using var handler = new FakeHandler(Json("""{"success":true,"obj":{"orderNo":"B123"}}"""));
        var client = new EsimAccessClient(new HttpClient(handler), Options.Create(new ProviderOptions
            { AccessCode = "test-access", SecretKey = secret }), Options.Create(new PaymentTestOptions()));
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.Equal("B123", await client.PlaceOrderAsync(transactionId, packageCode, 12345, default));

        var request = Assert.Single(handler.Requests);
        var timestamp = Assert.Single(request.Headers["RT-Timestamp"]);
        Assert.InRange(long.Parse(timestamp, CultureInfo.InvariantCulture), before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var requestId = Assert.Single(request.Headers["RT-RequestID"]);
        Assert.True(Guid.TryParseExact(requestId, "D", out _));
        Assert.Equal('4', requestId[14]);
        Assert.Equal("application/json; charset=utf-8", request.ContentType);
        Assert.Equal(Encoding.UTF8.GetBytes(request.Body), request.BodyBytes);
        using var body = JsonDocument.Parse(request.BodyBytes);
        Assert.Equal(transactionId, body.RootElement.GetProperty("transactionId").GetString());
        Assert.Equal(packageCode, body.RootElement.GetProperty("packageInfoList")[0].GetProperty("slug").GetString());
        AssertValidSignature(request, secret);
        Assert.DoesNotContain(secret, request.Body);
        Assert.DoesNotContain(request.Headers.Keys, key => key.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(request.Headers.Values.SelectMany(values => values), value => value.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachSignedReadRetryGetsFreshRequestIdAndSignatureForItsActualBody()
    {
        const string secret = "fake-signing-secret";
        using var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Json("""{"success":true,"obj":{"balance":123456}}"""));
        var client = new EsimAccessClient(new HttpClient(handler), Options.Create(new ProviderOptions
            { AccessCode = "test-access", SecretKey = secret }), Options.Create(new PaymentTestOptions()));

        Assert.Equal(123456, await client.GetBalanceUnitsAsync(default));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].BodyBytes, handler.Requests[1].BodyBytes);
        Assert.NotEqual(handler.Requests[0].Headers["RT-RequestID"].Single(), handler.Requests[1].Headers["RT-RequestID"].Single());
        Assert.NotEqual(handler.Requests[0].Headers["RT-Signature"].Single(), handler.Requests[1].Headers["RT-Signature"].Single());
        Assert.All(handler.Requests, request => AssertValidSignature(request, secret));
    }

    private static void AssertValidSignature(CapturedRequest request, string secret)
    {
        var prefix = request.Headers["RT-Timestamp"].Single() + request.Headers["RT-RequestID"].Single() + request.AccessCode;
        // Independent byte-based verification, without reserializing or normalizing the captured body.
        var signedBytes = Encoding.UTF8.GetBytes(prefix).Concat(request.BodyBytes).ToArray();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(signedBytes)).ToLowerInvariant();
        var actual = Assert.Single(request.Headers["RT-Signature"]);
        Assert.Matches("^[0-9a-f]{64}$", actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ProductionRejectsCopiedPaymentTestPackageWithoutHttp()
    {
        using var handler = new FakeHandler();
        var client = new EsimAccessClient(new HttpClient(handler), Options.Create(new ProviderOptions { AccessCode = "configured-live-key" }),
            Options.Create(new PaymentTestOptions { Enabled = false }));
        Assert.True(client.IsConfigured);
        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            client.PlaceOrderAsync("copied-order", PaymentTestOptions.PackageCode, 1, default));
        Assert.Equal("reserved_test_package", error.Code);
        Assert.False(error.Retryable);
        Assert.Empty(handler.Requests);
    }

    private static EsimAccessClient Client(FakeHandler handler) => new(new HttpClient(handler),
        Options.Create(new ProviderOptions { AccessCode = "test-key" }), Options.Create(new PaymentTestOptions()));

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bodyBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new(request.RequestUri!.AbsoluteUri, Encoding.UTF8.GetString(bodyBytes),
                request.Headers.TryGetValues("RT-AccessCode", out var values) ? values.Single() : null,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                bodyBytes, request.Content.Headers.ContentType?.ToString()));
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(string Url, string Body, string? AccessCode,
        IReadOnlyDictionary<string, string[]> Headers, byte[] BodyBytes, string? ContentType);
}

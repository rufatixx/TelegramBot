using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using Microsoft.Extensions.Options;

namespace EsimBot.BLL.Services;

/// <summary>eSIM Access production API. Purchasing is idempotent only with the same transaction ID and payload.</summary>
public sealed class EsimAccessClient(HttpClient httpClient, IOptions<ProviderOptions> options,
    IOptions<PaymentTestOptions> paymentTesting) : IEsimAccessClient
{
    private static readonly Uri ApiRoot = new("https://api.esimaccess.com/api/v1/open/");
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly SemaphoreSlim RequestsInFlight = new(4, 4);
    // Includes executing requests: a burst cannot retain an unbounded queue of supplier calls.
    private static readonly SemaphoreSlim RequestsAdmitted = new(32, 32);
    private static long _lastRequest;
    private readonly string _accessCode = options.Value.AccessCode?.Trim() ?? "";
    private readonly string _secretKey = options.Value.SecretKey ?? "";

    public bool IsConfigured => !paymentTesting.Value.Enabled && !string.IsNullOrWhiteSpace(_accessCode);

    public async Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct)
    {
        EnsureProductionMode();
        var obj = await ReadAsync("package/list", new { locationCode = "", type = "BASE", dataType = "1" }, ct);
        var result = new List<EsimPackage>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in RequiredArray(obj, "packageList").EnumerateArray())
        {
            // Daily/FUP plans need a duration selector and different price calculation. Never sell them as fixed plans.
            if (Text(item, "dataType") != "1") continue;
            if (RequiredText(item, "currencyCode") != "USD") continue;
            if (Text(item, "activeType") is not ("1" or "2")) continue;
            var code = RequiredText(item, "slug");
            ValidateIdentifier(code, 128);
            var price = RequiredLong(item, "price");
            var volume = RequiredLong(item, "volume");
            var duration = RequiredLong(item, "duration");
            var durationUnit = RequiredText(item, "durationUnit");
            if (price <= 0 || price > int.MaxValue || volume <= 0 || duration <= 0 || duration > int.MaxValue
                || durationUnit != "DAY" || !codes.Add(code)) throw InvalidResponse();
            var countries = ReadCountries(item);
            if (countries.Count == 0) throw InvalidResponse();
            var activation = Text(item, "activeType") switch
            {
                "1" => "install",
                "2" => "first_connection",
                _ => throw InvalidResponse()
            };
            result.Add(new EsimPackage(code, RequiredText(item, "name"), price, "USD", volume,
                (int)duration, durationUnit, countries, Text(item, "description") ?? "", activation,
                Text(item, "speed") ?? "", "1"));
        }
        return result;
    }

    public async Task<long> GetBalanceUnitsAsync(CancellationToken ct)
    {
        EnsureProductionMode();
        var obj = await ReadAsync("balance/query", new { }, ct);
        var balance = RequiredLong(obj, "balance");
        if (balance < 0) throw InvalidResponse();
        return balance;
    }

    public async Task<string> PlaceOrderAsync(string transactionId, string packageCode, long priceUnits, CancellationToken ct)
    {
        EnsureProductionMode();
        if (string.Equals(packageCode, PaymentTestOptions.PackageCode, StringComparison.Ordinal))
            throw new ProviderException("reserved_test_package", false);
        ValidateIdentifier(transactionId, 50);
        ValidateIdentifier(packageCode, 128);
        if (priceUnits <= 0 || priceUnits > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(priceUnits));
        // No client-side purchase retries. The durable worker may replay exactly this immutable payload.
        // Both amounts lock the quoted wholesale price; a price change must never silently charge more.
        try
        {
            var obj = await SendAsync("esim/order", new
            {
                transactionId,
                amount = priceUnits,
                packageInfoList = new[] { new { slug = packageCode, count = 1, price = priceUnits } }
            }, ct);
            return RequiredText(obj, "orderNo");
        }
        catch (ProviderException ex) when (ex.Code == "invalid_response")
        {
            // A successful upstream purchase may have produced a truncated response. Its outcome is unknown.
            throw new ProviderException("ambiguous_order_response", true);
        }
    }

    public async Task<ProviderOrder?> QueryOrderAsync(string transactionId, string? orderNumber, CancellationToken ct)
    {
        EnsureProductionMode();
        ValidateIdentifier(transactionId, 50);
        // The API documents orderNo/esimTranNo/ICCID lookup, not transactionId lookup.
        // An ambiguous purchase is recovered by replaying PlaceOrderAsync with the SAME transactionId.
        if (string.IsNullOrWhiteSpace(orderNumber)) return null;
        ValidateIdentifier(orderNumber, 128);
        JsonElement obj;
        try
        {
            obj = await ReadAsync("esim/query", new { orderNo = orderNumber, pager = new { pageNum = 1, pageSize = 10 } }, ct);
        }
        catch (ProviderException ex) when (ex.Code is "200010" or "310272")
        {
            return null;
        }
        var profiles = RequiredArray(obj, "esimList");
        if (profiles.GetArrayLength() == 0) return null;
        // This integration purchases exactly one eSIM per order. Unexpected multiplicity needs operator review.
        if (profiles.GetArrayLength() != 1) throw InvalidResponse();
        var item = profiles[0];
        if (RequiredText(item, "orderNo") != orderNumber) throw InvalidResponse();
        var status = RequiredText(item, "esimStatus");
        if (status is "CREATE" or "PAYING" or "PAID" or "GETTING_RESOURCE") return null;
        if (status is not ("GOT_RESOURCE" or "IN_USE")) throw new ProviderException("profile_not_deliverable", false);
        var activationCode = Text(item, "ac");
        if (string.IsNullOrEmpty(activationCode))
        {
            throw InvalidResponse();
        }
        if (!IsActivationCode(activationCode)) throw InvalidResponse();
        return new ProviderOrder(orderNumber,
        [new ProviderProfile(RequiredText(item, "esimTranNo"), RequiredText(item, "iccid"), activationCode,
            Text(item, "apn"), status)]);
    }

    public async Task<EsimUsage> GetUsageAsync(string esimId, CancellationToken ct)
    {
        EnsureProductionMode();
        ValidateIdentifier(esimId, 128);
        var obj = await ReadAsync("esim/query", new { esimTranNo = esimId, pager = new { pageNum = 1, pageSize = 10 } }, ct);
        var profiles = RequiredArray(obj, "esimList");
        if (profiles.GetArrayLength() != 1 || RequiredText(profiles[0], "esimTranNo") != esimId) throw InvalidResponse();
        var item = profiles[0];
        var used = RequiredLong(item, "orderUsage");
        var total = RequiredLong(item, "totalVolume");
        if (used < 0 || total < 0) throw InvalidResponse();
        DateTime? expiresAt = null;
        var expiration = Text(item, "expiredTime");
        if (!string.IsNullOrWhiteSpace(expiration))
        {
            if (!DateTimeOffset.TryParse(expiration, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed)) throw InvalidResponse();
            expiresAt = parsed.UtcDateTime;
        }
        return new EsimUsage(used, total, RequiredText(item, "esimStatus"), expiresAt);
    }

    private async Task<JsonElement> ReadAsync(string path, object body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await SendAsync(path, body, ct); }
            catch (ProviderException ex) when (ex.Retryable && attempt < 2
                && ex.Code is not ("200010" or "provider_busy"))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (1 << attempt)), ct);
            }
        }
    }

    private async Task<JsonElement> SendAsync(string path, object body, CancellationToken ct)
    {
        EnsureProductionMode();
        if (!IsConfigured) throw new ProviderException("not_configured", false);
        using var admission = await AcquireRequestAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoot, path));
        request.Headers.Add("RT-AccessCode", _accessCode);
        // Serialize once: the signature must cover exactly the UTF-8 JSON sent on the wire.
        var requestBody = JsonSerializer.Serialize(body, RequestJson);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        if (_secretKey.Length != 0)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var requestId = Guid.NewGuid().ToString("D");
            var signData = Encoding.UTF8.GetBytes(timestamp + requestId + _accessCode + requestBody);
            var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secretKey), signData);
            request.Headers.Add("RT-RequestID", requestId);
            request.Headers.Add("RT-Timestamp", timestamp);
            request.Headers.Add("RT-Signature", Convert.ToHexString(signature).ToLowerInvariant());
        }
        try
        {
            EnsureProductionMode();
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"http_{(int)response.StatusCode}", response.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            if (!root.TryGetProperty("success", out var success)) throw InvalidResponse();
            if (success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                && !(success.ValueKind == JsonValueKind.String && success.GetString() is "true" or "false")) throw InvalidResponse();
            if (success.ValueKind != JsonValueKind.True && !(success.ValueKind == JsonValueKind.String && success.GetString() == "true"))
            {
                var code = Text(root, "errorCode");
                if (string.IsNullOrWhiteSpace(code) || code.Length > 32 || !code.All(char.IsAsciiDigit)) code = "rejected";
                throw new ProviderException(code, code is "000001" or "900001" or "200010");
            }
            if (!root.TryGetProperty("obj", out var obj) || obj.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            return obj.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("timeout", true);
        }
        catch (HttpRequestException)
        {
            // Do not expose request headers, tokens, supplier payloads, or activation secrets in exception messages.
            throw new ProviderException("transport", true);
        }
        catch (IOException)
        {
            throw new ProviderException("transport", true);
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static async Task<RequestLease> AcquireRequestAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!RequestsAdmitted.Wait(0)) throw new ProviderException("provider_busy", true);
        var acquired = false;
        using var queueTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        queueTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await RequestsInFlight.WaitAsync(queueTimeout.Token);
            acquired = true;
            await ThrottleAsync(queueTimeout.Token);
            return new RequestLease();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (acquired) RequestsInFlight.Release();
            RequestsAdmitted.Release();
            throw new ProviderException("provider_busy", true);
        }
        catch
        {
            if (acquired) RequestsInFlight.Release();
            RequestsAdmitted.Release();
            throw;
        }
    }

    private sealed class RequestLease : IDisposable
    {
        public void Dispose()
        {
            RequestsInFlight.Release();
            RequestsAdmitted.Release();
        }
    }

    private static async Task ThrottleAsync(CancellationToken ct)
    {
        await RequestGate.WaitAsync(ct);
        try
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastRequest);
            var delay = TimeSpan.FromMilliseconds(140) - elapsed;
            if (_lastRequest != 0 && delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            _lastRequest = Stopwatch.GetTimestamp();
        }
        finally { RequestGate.Release(); }
    }

    private static IReadOnlyList<Country> ReadCountries(JsonElement item)
    {
        var location = RequiredText(item, "location");
        var countries = new List<Country>();
        foreach (var code in location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
        {
            if (code.Length != 2 || !code.All(c => c is >= 'A' and <= 'Z')) throw InvalidResponse();
            string name;
            try { name = new RegionInfo(code).EnglishName; }
            catch (ArgumentException) { name = code; }
            countries.Add(new Country(code, name));
        }
        return countries;
    }

    private static string? Text(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string RequiredText(JsonElement obj, string property) => Text(obj, property) is { Length: > 0 } text
        && !string.IsNullOrWhiteSpace(text) ? text : throw InvalidResponse();

    private static long RequiredLong(JsonElement obj, string property) => long.TryParse(Text(obj, property),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw InvalidResponse();

    private static JsonElement RequiredArray(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value : throw InvalidResponse();

    private static bool IsActivationCode(string value)
    {
        var parts = value.Split('$');
        return value.Length <= 2000 && !value.Any(char.IsControl) && parts.Length >= 3
            && parts[0] == "LPA:1" && !string.IsNullOrWhiteSpace(parts[1]) && !string.IsNullOrWhiteSpace(parts[2]);
    }

    private static void ValidateIdentifier(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > maxLength || value.Any(char.IsControl))
            throw new ArgumentException("Invalid provider identifier.");
    }

    private static ProviderException InvalidResponse() => new("invalid_response", false);

    private void EnsureProductionMode()
    {
        if (paymentTesting.Value.Enabled) throw new ProviderException("payment_test_mode", false);
    }
}

using System.Globalization;
using EsimBot.BLL.DTO;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;

namespace EsimBot.BLL.Services;

/// <summary>Persists language preferences through the existing application-state repository.</summary>
public sealed class LanguageService(IAppStateRepository preferences, IOptions<BotOptions> options) : ILanguageService, IDisposable
{
    // A fixed set of locks serializes concurrent automatic detection/manual choices without retaining user IDs in memory.
    private static readonly SemaphoreSlim[] UserGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 5000 });

    public async Task<BotText> GetAsync(long userId, string? telegramLanguageCode, CancellationToken ct)
    {
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(userId, out string? preference))
            {
                preference = await preferences.GetAsync(Key(userId), ct);
                Remember(userId, preference);
            }
            // A plain code is an explicit choice, which always wins over the Telegram profile language.
            if (IsSupported(preference)) return new BotText(preference!);
            var detected = preference?.StartsWith("auto:", StringComparison.Ordinal) == true
                ? preference[5..] : null;
            var language = telegramLanguageCode is null && IsSupported(detected)
                ? detected!
                : Normalize(telegramLanguageCode) ?? Normalize(options.Value.DefaultLanguage) ?? "en";
            var saved = "auto:" + language;
            if (preference != saved)
            {
                await preferences.SetAsync(Key(userId), saved, ct);
                Remember(userId, saved);
            }
            return new BotText(language);
        }
        finally { gate.Release(); }
    }

    public async Task SetAsync(long userId, string language, CancellationToken ct)
    {
        if (!IsSupported(language)) throw new ArgumentException("Unsupported bot language.", nameof(language));
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            await preferences.SetAsync(Key(userId), language, ct);
            Remember(userId, language);
        }
        finally { gate.Release(); }
    }

    private void Remember(long userId, string? preference) => _cache.Set(userId, preference,
        new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });

    public void Dispose() => _cache.Dispose();

    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var prefix = language.Trim().Split('-', '_')[0].ToLowerInvariant();
        return IsSupported(prefix) ? prefix : null;
    }

    private static bool IsSupported(string? language) => language is "az" or "ru" or "en";
    private static string Key(long userId) => "language:" + userId.ToString(CultureInfo.InvariantCulture);

    private static SemaphoreSlim Gate(long userId)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        return UserGates[(int)(userId % UserGates.Length)];
    }
}

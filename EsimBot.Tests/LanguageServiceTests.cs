using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace EsimBot.Tests;

public sealed class LanguageServiceTests
{
    [Theory]
    [InlineData("az-AZ", "az")]
    [InlineData("RU-ru", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("ru_RU", "ru")]
    public async Task TelegramLanguageIsNormalizedAndPersistedForAsyncNotifications(string telegramLanguage, string expected)
    {
        var preferences = new FakePreferences();
        var service = Service(preferences);
        Assert.Equal(expected, (await service.GetAsync(42, telegramLanguage, default)).Code);
        Assert.Equal("auto:" + expected, preferences.Values["language:42"]);
        // A fresh service instance models a restart; notifications do not include a Telegram user profile.
        Assert.Equal(expected, (await Service(preferences).GetAsync(42, null, default)).Code);
        Assert.Equal(1, preferences.Writes);
    }

    [Fact]
    public async Task ExplicitChoiceSurvivesRestartAndOverridesTelegramLocale()
    {
        var preferences = new FakePreferences();
        await Service(preferences).SetAsync(42, "az", default);
        Assert.Equal("az", (await Service(preferences).GetAsync(42, "ru-RU", default)).Code);
        Assert.Equal("az", preferences.Values["language:42"]);
    }

    [Fact]
    public async Task AutomaticLanguageMayFollowTelegramUntilManualChoice()
    {
        var preferences = new FakePreferences();
        var service = Service(preferences);
        await service.GetAsync(42, "en-US", default);
        Assert.Equal("ru", (await service.GetAsync(42, "ru-RU", default)).Code);
        await service.SetAsync(42, "az", default);
        Assert.Equal("az", (await service.GetAsync(42, "en-US", default)).Code);
    }

    [Theory]
    [InlineData("de-DE", "az", "az")]
    [InlineData(null, "ru", "ru")]
    [InlineData("fr", "invalid", "en")]
    [InlineData("", "en", "en")]
    public async Task UnsupportedTelegramLanguageUsesConfiguredDefault(string? telegramLanguage, string configured, string expected)
    {
        var preferences = new FakePreferences();
        Assert.Equal(expected, (await Service(preferences, configured).GetAsync(42, telegramLanguage, default)).Code);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("ru-RU")]
    [InlineData("EN")]
    [InlineData("")]
    public async Task UnsupportedManualPreferenceIsRejectedWithoutWriting(string language)
    {
        var preferences = new FakePreferences();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(preferences).SetAsync(42, language, default));
        Assert.Empty(preferences.Values);
    }

    [Fact]
    public async Task SimultaneousManualChoiceCannotBeOverwrittenByAutomaticDetection()
    {
        var preferences = new FakePreferences { BlockNextRead = true };
        var service = Service(preferences);
        var automatic = service.GetAsync(42, "ru", default);
        await preferences.ReadEntered.Task;
        var manual = service.SetAsync(42, "az", default);
        preferences.ContinueRead.SetResult();
        await Task.WhenAll(automatic, manual);
        Assert.Equal("az", preferences.Values["language:42"]);
        Assert.Equal("az", (await service.GetAsync(42, null, default)).Code);
    }

    [Theory]
    [InlineData("az", "geri qaytarıldı")]
    [InlineData("ru", "Возврат")]
    [InlineData("en", "refunded")]
    public async Task RefundNotificationUsesPersistedBuyerLanguage(string language, string expected)
    {
        var preferences = new FakePreferences();
        var languages = Service(preferences);
        await languages.SetAsync(42, language, default);
        using var http = new TelegramReplies();
        var notifier = new BotNotifier(new TelegramBotClient("123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi", new HttpClient(http)),
            Options.Create(new BotOptions()), languages);
        var order = new Order("order-1", 42, "package", "Plan", 10000, 123, "refunded", null, DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(10), 0, null);
        await notifier.RefundedAsync(order, default);
        var body = Assert.Single(http.Messages);
        Assert.Equal(42, body.GetProperty("chat_id").GetInt64());
        Assert.Contains(expected, body.GetProperty("text").GetString());
        Assert.True(body.GetProperty("protect_content").GetBoolean());
    }

    [Fact]
    public async Task AdminNotificationUsesAdminsOwnLanguageAndSanitizesStatus()
    {
        var preferences = new FakePreferences();
        var languages = Service(preferences);
        await languages.SetAsync(999, "az", default);
        using var http = new TelegramReplies();
        var notifier = new BotNotifier(new TelegramBotClient("123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi", new HttpClient(http)),
            Options.Create(new BotOptions { AdminUserId = 999 }), languages);
        await notifier.AlertAdminAsync("order-1", "retry_needed/<unsafe>", default);
        var body = Assert.Single(http.Messages);
        Assert.Equal(999, body.GetProperty("chat_id").GetInt64());
        Assert.Contains("yoxlamaq lazımdır", body.GetProperty("text").GetString());
        Assert.DoesNotContain("<", body.GetProperty("text").GetString());
    }

    private static LanguageService Service(FakePreferences store, string defaultLanguage = "en") =>
        new(store, Options.Create(new BotOptions { DefaultLanguage = defaultLanguage }));

    [Fact]
    public async Task Concurrent_language_reads_share_cached_preference_and_manual_choice_invalidates_it()
    {
        var preferences = new FakePreferences();
        using var service = Service(preferences);
        var results = await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => service.GetAsync(42, "ru", default)));
        Assert.All(results, result => Assert.Equal("ru", result.Code));
        Assert.Equal(1, preferences.Reads);
        Assert.Equal(1, preferences.Writes);
        await service.SetAsync(42, "az", default);
        Assert.Equal("az", (await service.GetAsync(42, "en", default)).Code);
        Assert.Equal(1, preferences.Reads);
        Assert.Equal("az", preferences.Values["language:42"]);
    }

    private sealed class FakePreferences : IAppStateRepository
    {
        public Task EnsureEnvironmentAsync(string environment, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteExpiredSupportStateAsync(long nowUnixSeconds, CancellationToken ct) => throw new NotSupportedException();
        public ConcurrentDictionary<string, string> Values { get; } = new();
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public bool BlockNextRead { get; set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> GetAsync(string key, CancellationToken ct)
        {
            Reads++;
            if (BlockNextRead)
            {
                BlockNextRead = false;
                ReadEntered.SetResult();
                await ContinueRead.Task.WaitAsync(ct);
            }
            return Values.GetValueOrDefault(key);
        }

        public Task SetAsync(string key, string preference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Values[key] = preference;
            Writes++;
            return Task.CompletedTask;
        }

        public Task VerifyAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TelegramReplies : HttpMessageHandler
    {
        public List<JsonElement> Messages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Messages.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{"message_id":1,"date":0,"chat":{"id":42,"type":"private"}}}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}

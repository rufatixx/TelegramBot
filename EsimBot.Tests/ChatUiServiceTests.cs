using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.Services;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EsimBot.Tests;

public sealed class ChatUiServiceTests
{
    [Fact]
    public async Task First_view_is_sent_and_later_views_edit_the_same_durable_screen()
    {
        var state = new FakeState();
        using var http = new TelegramHttp();
        using var client = new HttpClient(http);
        var service = Service(state, client);
        var keyboard = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData("Next", "next") });

        await service.ShowAsync(42, "First", keyboard, ParseMode.None, default);
        await service.ShowAsync(42, "Second", keyboard, ParseMode.None, default);

        Assert.Equal(["sendMessage", "editMessageText"], http.Methods);
        Assert.Equal("17", state.Values["ui:screen:42"]);
        Assert.Equal(17, http.Bodies[1].GetProperty("message_id").GetInt32());
        Assert.Equal("Second", http.Bodies[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Callback_context_edits_that_message_without_state_lookup()
    {
        var state = new FakeState { FailReads = true };
        using var http = new TelegramHttp();
        using var client = new HttpClient(http);
        var service = Service(state, client);
        using (service.BeginScreen(91))
            await service.ShowAsync(42, "Page", null, ParseMode.None, default);
        Assert.Equal(["editMessageText"], http.Methods);
        Assert.Equal(91, http.Bodies[0].GetProperty("message_id").GetInt32());
    }

    [Fact]
    public async Task Deleted_old_screen_is_replaced_and_new_position_is_saved()
    {
        var state = new FakeState();
        state.Values["ui:screen:42"] = "9";
        using var http = new TelegramHttp { MissingEdit = true };
        using var client = new HttpClient(http);
        await Service(state, client).ShowAsync(42, "Fresh", null, ParseMode.None, default);
        Assert.Equal(["editMessageText", "deleteMessage", "sendMessage"], http.Methods);
        Assert.Equal("17", state.Values["ui:screen:42"]);
    }

    [Fact]
    public async Task Tracked_invoice_qr_and_instructions_are_all_removed_before_the_next_screen()
    {
        var state = new FakeState();
        using var http = new TelegramHttp();
        using var client = new HttpClient(http);
        var service = Service(state, client);

        await service.TrackAsync(42, 31, replace: true, default);
        await service.TrackAsync(42, 32, replace: false, default);
        Assert.Equal("31,32", state.Values["ui:screen:42"]);

        await service.ClearAsync(42, default);

        Assert.Equal(["deleteMessage", "deleteMessage"], http.Methods);
        Assert.Equal("", state.Values["ui:screen:42"]);
        Assert.Equal([31, 32], http.Bodies.Select(body => body.GetProperty("message_id").GetInt32()));
    }

    [Fact]
    public async Task Clear_also_removes_callback_screen_when_durable_state_was_lost()
    {
        var state = new FakeState();
        using var http = new TelegramHttp();
        using var client = new HttpClient(http);
        var service = Service(state, client);

        using (service.BeginScreen(44))
            await service.ClearAsync(42, default);

        Assert.Equal(["deleteMessage"], http.Methods);
        Assert.Equal(44, http.Bodies[0].GetProperty("message_id").GetInt32());
    }

    [Fact]
    public async Task Replacing_tracked_media_removes_every_previous_piece()
    {
        var state = new FakeState();
        state.Values["ui:screen:42"] = "31,32";
        using var http = new TelegramHttp();
        using var client = new HttpClient(http);

        await Service(state, client).TrackAsync(42, 40, replace: true, default);

        Assert.Equal(["deleteMessage", "deleteMessage"], http.Methods);
        Assert.Equal("40", state.Values["ui:screen:42"]);
        Assert.Equal([31, 32], http.Bodies.Select(body => body.GetProperty("message_id").GetInt32()));
    }

    [Fact]
    public async Task Customer_command_is_deleted_best_effort()
    {
        var state = new FakeState();
        using var http = new TelegramHttp { MissingDelete = true };
        using var client = new HttpClient(http);
        await Service(state, client).DismissInputAsync(42, 5, default);
        Assert.Equal(["deleteMessage"], http.Methods);
    }

    private static ChatUiService Service(IAppStateRepository state, HttpClient client) => new(
        new TelegramBotClient(new TelegramBotClientOptions("123456:FAKE_TEST_TOKEN") { RetryCount = 0, RetryThreshold = 0 }, client),
        state, NullLogger<ChatUiService>.Instance);

    private sealed class FakeState : IAppStateRepository
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();
        public bool FailReads { get; init; }
        public Task<string?> GetAsync(string key, CancellationToken ct) => FailReads
            ? throw new InvalidOperationException("Read should not happen.")
            : Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken ct)
        { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteExpiredSupportStateAsync(long nowUnixSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task VerifyAsync(CancellationToken ct) => Task.CompletedTask;
        public Task EnsureEnvironmentAsync(string environment, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TelegramHttp : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public List<JsonElement> Bodies { get; } = [];
        public bool MissingEdit { get; init; }
        public bool MissingDelete { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.Segments.Last().Trim('/');
            Methods.Add(method);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Bodies.Add(document.RootElement.Clone());
            if (method == "editMessageText" && MissingEdit || method == "deleteMessage" && MissingDelete)
                return Error("Bad Request: message to edit not found");
            var result = method == "deleteMessage" ? "true"
                : "{\"message_id\":17,\"date\":0,\"chat\":{\"id\":42,\"type\":\"private\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
        }
        private static HttpResponseMessage Error(string description) => new(HttpStatusCode.BadRequest)
        { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = 400, description }), Encoding.UTF8, "application/json") };
    }
}

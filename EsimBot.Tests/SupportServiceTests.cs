using System.Net;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EsimBot.Tests;

public sealed class SupportServiceTests
{
    private const long Customer = 42;
    private const long Admin = 99;

    [Fact]
    public async Task Ordinary_text_is_never_relayed_without_explicit_support_mode()
    {
        using var f = new Fixture();
        Assert.False(await f.Service.HandleAsync(Message(Customer, "ordinary text"), default));
        Assert.Empty(f.Http.Messages);
        Assert.Empty(f.State.Values);
    }

    [Theory]
    [InlineData("ru", false)]
    [InlineData("en", true)]
    [InlineData("az", true)]
    public async Task Prompt_discloses_next_text_relay_before_mode_is_enabled(string language, bool payment)
    {
        using var f = new Fixture();
        await f.Languages.SetAsync(Customer, language, default);
        await f.Service.BeginAsync(Customer, payment, default);
        var prompt = Assert.Single(f.Http.Messages);
        Assert.Equal(Customer, prompt.ChatId);
        Assert.Equal(new BotText(language).Text(payment ? "support.payment_prompt" : "support.prompt"), prompt.Text);
        Assert.DoesNotContain(Admin.ToString(), prompt.Text);
        Assert.Contains("/cancel", prompt.Text);
        Assert.True(prompt.Body.GetProperty("reply_markup").GetProperty("force_reply").GetBoolean());
        Assert.Equal(new BotText(language).Text("support.input_placeholder"),
            prompt.Body.GetProperty("reply_markup").GetProperty("input_field_placeholder").GetString());
        using var mode = JsonDocument.Parse(f.State.Values["support:mode:42"]);
        Assert.Equal(payment ? "payment" : "general", mode.RootElement.GetProperty("topic").GetString());
        Assert.InRange(mode.RootElement.GetProperty("expiresAt").GetInt64() - Now(), 1790, 1800);
    }

    [Theory]
    [InlineData("ru", false)]
    [InlineData("en", true)]
    [InlineData("az", true)]
    public async Task Administrator_can_try_the_customer_flow_and_reply_to_the_explicit_test_ticket(string language, bool payment)
    {
        using var f = new Fixture();
        await f.Languages.SetAsync(Admin, language, default);
        await f.Service.BeginAsync(Admin, payment, default);
        var prompt = Assert.Single(f.Http.Messages);
        Assert.Equal(new BotText(language).Text("support.admin_preview"), prompt.Text);
        using (var mode = JsonDocument.Parse(f.State.Values["support:mode:99"]))
        {
            Assert.True(mode.RootElement.GetProperty("preview").GetBoolean());
            Assert.Equal(prompt.Id, mode.RootElement.GetProperty("promptMessageId").GetInt32());
        }
        f.Http.Messages.Clear();
        var request = Message(Admin, "my test question");
        request.ReplyToMessage = Delivered(prompt);

        Assert.True(await f.Service.HandleAsync(request, default));

        Assert.Equal(2, f.Http.Messages.Count);
        var ticket = f.Http.Messages[0];
        Assert.Equal(new BotText(language).Text("support.preview_incoming", Admin,
            new BotText(language).Text(payment ? "support.topic_payment" : "support.topic_general"), request.Text!), ticket.Text);
        Assert.All(f.Http.Messages, message => Assert.Equal(Admin, message.ChatId));
        Assert.Equal(new BotText(language).Text("support.preview_sent"), f.Http.Messages.Last().Text);
        Assert.DoesNotContain("support:mode:99", f.State.Values.Keys);
        var route = Assert.Single(f.State.Values, pair => pair.Key.StartsWith("support:route:"));
        using (var entry = JsonDocument.Parse(route.Value)) Assert.True(entry.RootElement.GetProperty("preview").GetBoolean());

        f.Http.Messages.Clear();
        var reply = Message(Admin, "my test answer");
        reply.ReplyToMessage = Delivered(ticket);
        Assert.True(await f.NewService().HandleAsync(reply, default));
        Assert.Equal(new BotText(language).Text("support.reply_received", reply.Text!), f.Http.Messages[0].Text);
        Assert.Equal(new BotText(language).Text("support.reply_sent"), f.Http.Messages[1].Text);
        Assert.All(f.Http.Messages, message => Assert.Equal(Admin, message.ChatId));
        Assert.DoesNotContain(f.State.Values.Keys, key => key.StartsWith("support:route:"));
    }

    [Fact]
    public async Task Administrator_preview_accepts_plain_text_without_manually_using_reply()
    {
        using var f = new Fixture();
        await f.Service.BeginAsync(Admin, false, default);
        f.Http.Messages.Clear();
        Assert.True(await f.Service.HandleAsync(Message(Admin, "question"), default));
        Assert.Equal(new BotText("en").Text("support.preview_sent"), f.Http.Messages.Last().Text);
    }

    [Fact]
    public async Task Actual_customer_reply_remains_routed_to_customer_while_admin_is_in_preview()
    {
        using var f = new Fixture();
        var ticket = await f.CreateTicketAsync();
        await f.Service.BeginAsync(Admin, false, default);
        f.Http.Messages.Clear();
        var reply = Message(Admin, "real answer");
        reply.ReplyToMessage = Delivered(ticket);
        Assert.True(await f.Service.HandleAsync(reply, default));
        Assert.Equal(new BotText("en").Text("support.reply_received", reply.Text!),
            Assert.Single(f.Http.Messages, message => message.ChatId == Customer).Text);
        Assert.Contains("support:mode:99", f.State.Values.Keys);
        Assert.DoesNotContain(f.Http.Messages, message => message.Text.Contains("Support test"));
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/start")]
    [InlineData("/refund some-order")]
    public async Task Administrator_commands_cancel_preview_and_are_not_forwarded(string command)
    {
        using var f = new Fixture();
        await f.Service.BeginAsync(Admin, false, default);
        f.Http.Messages.Clear();
        Assert.False(await f.Service.HandleAsync(Message(Admin, command), default));
        Assert.Empty(f.Http.Messages);
        Assert.DoesNotContain("support:mode:99", f.State.Values.Keys);
    }

    [Fact]
    public async Task Self_reply_is_rejected_without_explicit_server_side_preview_flag()
    {
        using var f = new Fixture();
        f.State.Values["support:route:99:1234"] = JsonSerializer.Serialize(new { expiresAt = Now() + 1000, userId = Admin });
        var reply = Message(Admin, "answer");
        reply.ReplyToMessage = Message(Admin, "forged preview");
        reply.ReplyToMessage.Id = 1234;
        reply.ReplyToMessage.From!.IsBot = true;
        Assert.True(await f.Service.HandleAsync(reply, default));
        Assert.Equal(new BotText("en").Text("support.reply_invalid"), Assert.Single(f.Http.Messages).Text);
    }

    [Fact]
    public async Task Only_opted_in_text_is_sent_as_protected_plain_text_without_persisting_the_ticket()
    {
        using var f = new Fixture();
        await f.Languages.SetAsync(Admin, "ru", default);
        await f.Service.BeginAsync(Customer, true, default);
        f.Http.Messages.Clear();
        const string text = "<b>private ticket body</b> @someone https://example.test";
        Assert.True(await f.Service.HandleAsync(Message(Customer, text), default));
        var ticket = Assert.Single(f.Http.Messages, x => x.ChatId == Admin);
        Assert.Equal(new BotText("ru").Text("support.incoming", Customer, new BotText("ru").Text("support.topic_payment"), text), ticket.Text);
        Assert.True(ticket.Body.GetProperty("protect_content").GetBoolean());
        Assert.False(ticket.Body.TryGetProperty("parse_mode", out _));
        Assert.True(ticket.Body.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
        Assert.DoesNotContain("support:mode:42", f.State.Values.Keys);
        var route = Assert.Single(f.State.Values, x => x.Key.StartsWith("support:route:"));
        using var stored = JsonDocument.Parse(route.Value);
        Assert.Equal(Customer, stored.RootElement.GetProperty("userId").GetInt64());
        Assert.InRange(stored.RootElement.GetProperty("expiresAt").GetInt64() - Now(), 604790, 604800);
        Assert.All(f.State.Values.Values, value => Assert.DoesNotContain("private ticket body", value));
        Assert.Equal(new BotText("en").Text("support.sent"), f.Http.Messages.Last().Text);
        Assert.False(await f.Service.HandleAsync(Message(Customer, "do not forward this"), default));
    }

    [Fact]
    public async Task Reply_survives_service_restart_and_uses_route_not_forged_customer_text()
    {
        using var f = new Fixture();
        await f.Languages.SetAsync(Customer, "az", default);
        var ticket = await f.CreateTicketAsync();
        f.Http.Messages.Clear();
        // Forged text and sender names cannot change the stored destination.
        var reply = Message(Admin, "<b>answer</b> customer: 777");
        reply.ReplyToMessage = Delivered(ticket, "Customer: 777");
        Assert.True(await f.NewService().HandleAsync(reply, default));
        var delivered = Assert.Single(f.Http.Messages, x => x.ChatId == Customer);
        Assert.Equal(new BotText("az").Text("support.reply_received", reply.Text!), delivered.Text);
        Assert.True(delivered.Body.GetProperty("protect_content").GetBoolean());
        Assert.False(delivered.Body.TryGetProperty("parse_mode", out _));
        Assert.DoesNotContain(f.State.Values.Keys, key => key.StartsWith("support:route:"));
        f.Http.Messages.Clear();
        await f.Service.HandleAsync(reply, default);
        Assert.All(f.Http.Messages, x => Assert.Equal(Admin, x.ChatId));
        Assert.Equal(new BotText("en").Text("support.reply_invalid"), Assert.Single(f.Http.Messages).Text);
    }

    [Fact]
    public async Task Forged_reply_without_server_route_cannot_target_a_customer()
    {
        using var f = new Fixture();
        var reply = Message(Admin, "answer");
        reply.ReplyToMessage = Message(Admin, "Customer: 42");
        reply.ReplyToMessage.Id = 1234;
        reply.ReplyToMessage.From = new User { Id = 123456, IsBot = true, FirstName = "Support" };
        Assert.True(await f.Service.HandleAsync(reply, default));
        Assert.Equal(Admin, Assert.Single(f.Http.Messages).ChatId);
        Assert.Equal(new BotText("en").Text("support.reply_invalid"), f.Http.Messages[0].Text);
    }

    [Theory]
    [InlineData("nonadmin")]
    [InlineData("group")]
    [InlineData("senderbot")]
    [InlineData("chatmismatch")]
    [InlineData("notbotreply")]
    [InlineData("wrongreplychat")]
    public async Task Reply_checks_admin_private_chat_and_bot_origin(string attack)
    {
        using var f = new Fixture();
        var ticket = await f.CreateTicketAsync();
        f.Http.Messages.Clear();
        var reply = Message(Admin, "answer");
        reply.ReplyToMessage = Delivered(ticket);
        switch (attack)
        {
            case "nonadmin": reply.From!.Id = reply.Chat.Id = 777; break;
            case "group": reply.Chat.Type = ChatType.Group; break;
            case "senderbot": reply.From!.IsBot = true; break;
            case "chatmismatch": reply.Chat.Id = Customer; break;
            case "notbotreply": reply.ReplyToMessage.From!.IsBot = false; break;
            case "wrongreplychat": reply.ReplyToMessage.Chat.Id = Customer; break;
        }
        await f.Service.HandleAsync(reply, default);
        Assert.DoesNotContain(f.Http.Messages, x => x.ChatId == Customer || x.ChatId == 777);
        Assert.Contains(f.State.Values.Keys, key => key.StartsWith("support:route:"));
    }

    [Fact]
    public async Task Reply_command_never_parses_user_id_as_destination()
    {
        using var f = new Fixture();
        Assert.True(await f.Service.HandleAsync(Message(Admin, "/reply 42 forged answer"), default));
        Assert.Equal(Admin, Assert.Single(f.Http.Messages).ChatId);
        Assert.Equal(new BotText("en").Text("support.admin_help"), f.Http.Messages[0].Text);
    }

    [Theory]
    [InlineData("/refund order-id")]
    [InlineData("/retry order-id")]
    [InlineData("/orders")]
    [InlineData("/start")]
    public async Task Administrator_commands_sent_as_reply_are_not_relayed(string command)
    {
        using var f = new Fixture();
        var ticket = await f.CreateTicketAsync();
        f.Http.Messages.Clear();
        var reply = Message(Admin, command);
        reply.ReplyToMessage = Delivered(ticket);
        Assert.False(await f.Service.HandleAsync(reply, default));
        Assert.Empty(f.Http.Messages);
        Assert.Contains(f.State.Values.Keys, key => key.StartsWith("support:route:"));
    }

    [Theory]
    [InlineData("/cancel", true)]
    [InlineData("/orders", false)]
    [InlineData("/start@my_bot", false)]
    public async Task Cancel_and_other_commands_exit_support_without_forwarding(string command, bool handled)
    {
        using var f = new Fixture();
        await f.Service.BeginAsync(Customer, false, default);
        f.Http.Messages.Clear();
        Assert.Equal(handled, await f.Service.HandleAsync(Message(Customer, command), default));
        Assert.DoesNotContain("support:mode:42", f.State.Values.Keys);
        Assert.False(await f.Service.HandleAsync(Message(Customer, "not a ticket"), default));
        Assert.DoesNotContain(f.Http.Messages, x => x.ChatId == Admin);
    }

    [Fact]
    public async Task Callback_navigation_can_silently_cancel_support()
    {
        using var f = new Fixture();
        await f.Service.BeginAsync(Customer, false, default);
        f.Http.Messages.Clear();
        await f.Service.CancelAsync(Customer, default);
        Assert.Empty(f.Http.Messages);
        Assert.False(await f.Service.HandleAsync(Message(Customer, "not a ticket"), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_modes_and_routes_are_rejected_even_if_bulk_cleanup_is_unavailable(bool route)
    {
        using var f = new Fixture();
        Message request;
        string key;
        if (route)
        {
            var ticket = await f.CreateTicketAsync();
            key = $"support:route:{Admin}:{ticket.Id}";
            request = Message(Admin, "answer");
            request.ReplyToMessage = Delivered(ticket);
        }
        else
        {
            await f.Service.BeginAsync(Customer, false, default);
            key = "support:mode:42";
            request = Message(Customer, "expired ticket");
        }
        f.State.Values[key] = JsonSerializer.Serialize(new { expiresAt = Now() - 1, userId = Customer, topic = "general" });
        f.State.FailCleanup = true;
        f.Http.Messages.Clear();
        await f.NewService().HandleAsync(request, default);
        Assert.DoesNotContain(key, f.State.Values.Keys);
        Assert.DoesNotContain(f.Http.Messages, x => x.ChatId == Customer);
    }

    [Fact]
    public async Task Expired_routes_are_cleaned_without_touching_unrelated_app_state()
    {
        using var f = new Fixture();
        f.State.Values["support:route:99:1"] = JsonSerializer.Serialize(new { expiresAt = Now() - 1, userId = Customer });
        f.State.Values["support:mode:42"] = JsonSerializer.Serialize(new { expiresAt = Now() - 1, topic = "general" });
        f.State.Values["language:42"] = "ru";
        await f.Service.HandleAsync(Message(Customer, "hello"), default);
        Assert.Equal("language:42", Assert.Single(f.State.Values).Key);
        await f.Service.HandleAsync(Message(Customer, "another"), default);
        Assert.Equal(1, f.State.Cleanups);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-long")]
    public async Task Files_captions_empty_or_oversized_messages_are_not_forwarded(string? input)
    {
        using var f = new Fixture();
        await f.Service.BeginAsync(Customer, false, default);
        f.Http.Messages.Clear();
        var message = Message(Customer, input == "too-long" ? new string('a', 3001) : input);
        message.Caption = "secret attachment caption";
        Assert.True(await f.Service.HandleAsync(message, default));
        Assert.Equal(Customer, Assert.Single(f.Http.Messages).ChatId);
        Assert.Contains("support:mode:42", f.State.Values.Keys);
    }

    [Fact]
    public async Task Administrator_send_failure_is_not_reported_as_success_and_can_be_retried()
    {
        using var f = new Fixture();
        await f.Service.BeginAsync(Customer, false, default);
        f.Http.Messages.Clear();
        f.Http.FailedChat = Admin;
        Assert.True(await f.Service.HandleAsync(Message(Customer, "ticket"), default));
        Assert.Equal(new BotText("en").Text("support.send_failed"), f.Http.Messages.Last().Text);
        Assert.Contains("support:mode:42", f.State.Values.Keys);
        Assert.DoesNotContain(f.State.Values.Keys, key => key.StartsWith("support:route:"));
        f.Http.FailedChat = null;
        Assert.True(await f.Service.HandleAsync(Message(Customer, "ticket"), default));
        Assert.Equal(new BotText("en").Text("support.sent"), f.Http.Messages.Last().Text);
    }

    [Fact]
    public async Task Reply_failure_does_not_report_success_or_remove_reply_route()
    {
        using var f = new Fixture();
        var ticket = await f.CreateTicketAsync();
        f.Http.Messages.Clear();
        f.Http.FailedChat = Customer;
        var reply = Message(Admin, "answer");
        reply.ReplyToMessage = Delivered(ticket);
        Assert.True(await f.Service.HandleAsync(reply, default));
        Assert.Equal(new BotText("en").Text("support.reply_failed"), f.Http.Messages.Last().Text);
        Assert.Contains(f.State.Values.Keys, key => key.StartsWith("support:route:"));
        f.Http.FailedChat = null;
        await f.Service.HandleAsync(reply, default);
        Assert.Equal(new BotText("en").Text("support.reply_sent"), f.Http.Messages.Last().Text);
    }

    [Fact]
    public async Task Missing_administrator_does_not_accept_ticket_mode()
    {
        using var f = new Fixture(0);
        await f.Service.BeginAsync(Customer, false, default);
        Assert.Equal(new BotText("en").Text("support.unavailable"), Assert.Single(f.Http.Messages).Text);
        Assert.Empty(f.State.Values);
    }

    [Fact]
    public async Task Support_prompt_and_customer_reply_follow_single_screen_policy()
    {
        var ui = new FakeChatUi();
        using var f = new Fixture(chatUi: ui);

        await f.Service.BeginAsync(Customer, false, default);
        var prompt = Assert.Single(f.Http.Messages);
        Assert.Equal([Customer], ui.Cleared);
        Assert.Equal((Customer, prompt.Id, true), Assert.Single(ui.Tracked));

        f.Http.Messages.Clear();
        Assert.True(await f.Service.HandleAsync(Message(Customer, "question"), default));

        Assert.Contains((Customer, 1), ui.Dismissed);
        Assert.Equal(new BotText("en").Text("support.sent"), Assert.Single(ui.Shown).Text);
        Assert.Single(f.Http.Messages, message => message.ChatId == Admin);
    }

    [Theory]
    [InlineData("az")]
    [InlineData("ru")]
    [InlineData("en")]
    public void Longest_ticket_and_builtin_terms_fit_Telegram_limits(string language)
    {
        var t = new BotText(language);
        Assert.InRange(t.Text("support.incoming", long.MaxValue, t.Text("support.topic_general"), new string('a', 3000)).Length, 1, 4096);
        Assert.InRange(t.Text("support.reply_received", new string('a', 3000)).Length, 1, 4096);
        Assert.InRange((t.Text("support.terms_builtin") + t.Text("ui.terms_version_note", new string('v', 80))).Length, 1, 4096);
        Assert.Contains("XTR", t.Text("support.terms_builtin"));
        Assert.Contains("/paysupport", t.Text("support.terms_builtin"));
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static Message Message(long userId, string? text) => new()
    {
        Id = 1, Chat = new Chat { Id = userId, Type = ChatType.Private },
        From = new User { Id = userId, FirstName = "User", LanguageCode = "en" }, Text = text
    };
    private static Message Delivered(SentMessage message, string? forgedText = null) => new()
    {
        Id = message.Id, Chat = new Chat { Id = message.ChatId, Type = ChatType.Private },
        From = new User { Id = 123456, IsBot = true, FirstName = "Bot" }, Text = forgedText ?? message.Text
    };

    private sealed class Fixture : IDisposable
    {
        public FakeState State { get; } = new();
        public FakeLanguages Languages { get; } = new();
        public FakeTelegram Http { get; } = new();
        private readonly HttpClient _http;
        private readonly TelegramBotClient _bot;
        private readonly IOptions<BotOptions> _options;
        public SupportService Service { get; }
        private readonly IChatUiService? _chatUi;
        public Fixture(long admin = Admin, IChatUiService? chatUi = null)
        {
            _http = new HttpClient(Http);
            _bot = new TelegramBotClient("123456:TEST_TOKEN_NOT_A_REAL_SECRET", _http);
            _options = Options.Create(new BotOptions { AdminUserId = admin });
            _chatUi = chatUi;
            Service = NewService();
        }
        public SupportService NewService() => new(_bot, State, Languages, _options,
            NullLogger<SupportService>.Instance, _chatUi);
        public async Task<SentMessage> CreateTicketAsync()
        {
            await Service.BeginAsync(Customer, false, default);
            await Service.HandleAsync(Message(Customer, "ticket"), default);
            return Assert.Single(Http.Messages, x => x.ChatId == Admin);
        }
        public void Dispose() => _http.Dispose();
    }

    private sealed class FakeChatUi : IChatUiService
    {
        public List<long> Cleared { get; } = [];
        public List<(long User, int Message, bool Replace)> Tracked { get; } = [];
        public List<(long User, int Message)> Dismissed { get; } = [];
        public List<(long User, string Text)> Shown { get; } = [];
        public IDisposable BeginScreen(int? messageId) => new EmptyScope();
        public Task ClearAsync(long userId, CancellationToken ct) { Cleared.Add(userId); return Task.CompletedTask; }
        public Task TrackAsync(long userId, int messageId, bool replace, CancellationToken ct)
        { Tracked.Add((userId, messageId, replace)); return Task.CompletedTask; }
        public Task DismissInputAsync(long userId, int messageId, CancellationToken ct)
        { Dismissed.Add((userId, messageId)); return Task.CompletedTask; }
        public Task ShowAsync(long userId, string text, Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup? keyboard,
            ParseMode parseMode, CancellationToken ct)
        { Shown.Add((userId, text)); return Task.CompletedTask; }
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeLanguages : ILanguageService
    {
        private readonly Dictionary<long, string> _languages = [];
        public Task<BotText> GetAsync(long userId, string? telegramLanguageCode, CancellationToken ct)
            => Task.FromResult(new BotText(_languages.GetValueOrDefault(userId, telegramLanguageCode ?? "en")));
        public Task SetAsync(long userId, string language, CancellationToken ct)
        { _languages[userId] = language; return Task.CompletedTask; }
    }

    private sealed class FakeState : IAppStateRepository
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailCleanup { get; set; }
        public int Cleanups { get; private set; }
        public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken ct)
        { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct)
        { Values.Remove(key); return Task.CompletedTask; }
        public Task DeleteExpiredSupportStateAsync(long nowUnixSeconds, CancellationToken ct)
        {
            if (FailCleanup) throw new InvalidOperationException("cleanup unavailable");
            Cleanups++;
            foreach (var pair in Values.ToArray().Where(x => x.Key.StartsWith("support:mode:") || x.Key.StartsWith("support:route:")))
            {
                using var entry = JsonDocument.Parse(pair.Value);
                if (entry.RootElement.GetProperty("expiresAt").GetInt64() <= nowUnixSeconds) Values.Remove(pair.Key);
            }
            return Task.CompletedTask;
        }
        public Task VerifyAsync(CancellationToken ct) => Task.CompletedTask;
        public Task EnsureEnvironmentAsync(string environment, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record SentMessage(int Id, long ChatId, string Text, JsonElement Body);
    private sealed class FakeTelegram : HttpMessageHandler
    {
        public List<SentMessage> Messages { get; } = [];
        public long? FailedChat { get; set; }
        private int _nextId;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/sendMessage", request.RequestUri!.AbsolutePath);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = doc.RootElement.Clone();
            var chat = body.GetProperty("chat_id").GetInt64();
            var id = ++_nextId;
            Messages.Add(new SentMessage(id, chat, body.GetProperty("text").GetString()!, body));
            var result = chat == FailedChat
                ? """{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}"""
                : JsonSerializer.Serialize(new { ok = true, result = new { message_id = id, date = 0,
                    chat = new { id = chat, type = "private" }, from = new { id = 123456, is_bot = true, first_name = "Bot" } } });
            return new HttpResponseMessage(chat == FailedChat ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}

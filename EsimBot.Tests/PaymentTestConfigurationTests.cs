using System.Net;
using System.Text;
using EsimBot.BLL.DTO;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;

namespace EsimBot.Tests;

public sealed class PaymentTestConfigurationTests
{
    private const string ProductionToken = "111111:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";
    private const string TestToken = "222222:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";

    [Fact]
    public void Test_mode_selects_only_test_identity_and_forces_polling()
    {
        var options = BotOptions.Resolve(Configuration(true));
        Assert.True(options.TestEnvironment);
        Assert.Equal(TestToken, options.Token);
        Assert.Equal(202, options.AdminUserId);
        Assert.Equal("Polling", options.Mode);
        Assert.Equal("", options.WebhookUrl);
        Assert.Equal("", options.WebhookSecret);
        Assert.Equal("az", options.DefaultLanguage);
    }

    [Fact]
    public void Missing_test_token_does_not_fall_back_to_real_bot()
    {
        var config = Configuration(true);
        config["PaymentTesting:BotToken"] = "";
        var options = BotOptions.Resolve(config);
        Assert.Equal("", options.Token);
        Assert.False(StartupConfiguration.Inspect(config).CanStartWorker);
    }

    [Fact]
    public void Only_the_single_test_switch_can_change_the_Telegram_environment()
    {
        var config = Configuration(false);
        config["TelegramBot:TestEnvironment"] = "true";
        var options = BotOptions.Resolve(config);
        Assert.False(options.TestEnvironment);
        Assert.Equal(ProductionToken, options.Token);
        Assert.Equal(101, options.AdminUserId);
        Assert.Equal("Webhook", options.Mode);
    }

    [Fact]
    public void Sandbox_is_ready_without_supplier_or_sales_configuration_but_never_can_sell()
    {
        var readiness = StartupConfiguration.Inspect(Configuration(true));
        Assert.True(readiness.TestMode);
        Assert.True(readiness.CanStartWorker);
        Assert.True(readiness.CanTestPayments);
        Assert.False(readiness.CanSell);
        Assert.False(readiness.ProviderConfigured);
        Assert.False(readiness.SalesConfigured);
        Assert.Equal("test", readiness.PaymentEnvironment);
    }

    [Fact]
    public void Missing_test_connection_cannot_use_the_existing_production_connection()
    {
        var config = Configuration(true);
        config["ConnectionStrings:EsimBotTest"] = "";
        Assert.False(StartupConfiguration.Inspect(config).DatabaseConfigured);
        Assert.Throws<InvalidOperationException>(() => DatabaseEnvironment.ResolveConnectionString(config));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Test_invoice_amount_is_bounded(int stars, bool expected)
        => Assert.Equal(expected, new PaymentTestOptions
        { Enabled = true, BotToken = TestToken, AdminUserId = 202, InvoiceStars = stars }.IsConfigured);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Telegram_SDK_routes_requests_to_the_selected_environment(bool testing)
    {
        var options = BotOptions.Resolve(Configuration(testing));
        using var transport = new RecordingTelegramHandler();
        using var http = new HttpClient(transport);
        var client = new TelegramBotClient(new TelegramBotClientOptions(options.Token,
            useTestEnvironment: options.TestEnvironment), http);
        await client.GetMe();
        var path = Assert.Single(transport.Paths);
        Assert.Equal(testing ? $"/bot{TestToken}/test/getMe" : $"/bot{ProductionToken}/getMe", path);
    }

    [Fact]
    public void Health_status_never_discloses_test_tokens_or_connection_passwords()
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(StartupConfiguration.Inspect(Configuration(true)));
        Assert.DoesNotContain(TestToken, serialized);
        Assert.DoesNotContain(ProductionToken, serialized);
        Assert.DoesNotContain("private-password", serialized);
    }

    private static IConfigurationRoot Configuration(bool testing) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PaymentTesting:Enabled"] = testing.ToString(),
            ["PaymentTesting:BotToken"] = TestToken,
            ["PaymentTesting:AdminUserId"] = "202",
            ["PaymentTesting:InvoiceStars"] = "1",
            ["TelegramBot:Token"] = ProductionToken,
            ["TelegramBot:AdminUserId"] = "101",
            ["TelegramBot:DefaultLanguage"] = "az",
            ["TelegramBot:Mode"] = "Webhook",
            ["TelegramBot:WebhookUrl"] = "https://example.test/api/telegram/webhook",
            ["TelegramBot:WebhookSecret"] = "production-secret",
            ["ConnectionStrings:EsimBot"] = "Server=localhost;Database=esim_bot;User ID=esim_bot_app;Password=private-password;SslMode=Required;",
            ["ConnectionStrings:EsimBotTest"] = "Server=localhost;Database=esim_bot_test;User ID=esim_bot_test_app;Password=private-password;SslMode=Required;",
            ["Storage:EncryptionKey"] = Convert.ToBase64String(new byte[32])
        }).Build();

    private sealed class RecordingTelegramHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{"id":222222,"is_bot":true,"first_name":"Test","username":"test_bot"}}""", Encoding.UTF8, "application/json")
            });
        }
    }
}

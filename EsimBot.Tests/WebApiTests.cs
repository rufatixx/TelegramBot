using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.View.ApiControllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;

namespace EsimBot.Tests;

public sealed class WebApiTests
{
    [Theory]
    [InlineData("abc_DEF-123", "abc_DEF-123", true)]
    [InlineData("abc_DEF-123", "abc_DEF-124", false)]
    [InlineData("abc", "abcx", false)]
    [InlineData("", "", false)]
    [InlineData("abc", null, false)]
    [InlineData("a b", "a b", false)]
    [InlineData("тест", "тест", false)]
    public void WebhookSecretComparisonChecksFormatAndExactValue(string expected, string? received, bool valid)
        => Assert.Equal(valid, WebhookSecurity.VerifyWebhookSecret(expected, received));

    [Fact]
    public void OversizedWebhookSecretIsRejected()
        => Assert.False(WebhookSecurity.IsWebhookSecretValid(new string('a', 257)));

    [Fact]
    public void EmptyConfigIsNotReadyAndDoesNotContainSecrets()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EsimBot"] = "Server=localhost;Database=esim_bot;User ID=app;Password=secret-value;SslMode=Required;",
            ["Storage:EncryptionKey"] = "invalid",
            ["TelegramBot:WebhookSecret"] = "my-secret-value",
            ["Provider:AccessCode"] = "provider-secret"
        }).Build();
        var readiness = StartupConfiguration.Inspect(config);
        Assert.True(readiness.DatabaseConfigured);
        Assert.False(readiness.StorageConfigured);
        Assert.False(readiness.CanStartWorker);
        Assert.False(readiness.CanSell);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(readiness));
    }

    [Theory]
    [InlineData("Polling", true, "correct-secret", "{}", 404)]
    [InlineData("Webhook", true, "wrong-secret", "{}", 401)]
    [InlineData("Webhook", false, "correct-secret", "{}", 503)]
    [InlineData("Webhook", true, "correct-secret", "{bad-json", 400)]
    [InlineData("Webhook", true, "correct-secret", "null", 400)]
    [InlineData("Webhook", true, "correct-secret", "{\"update_id\":42}", 200)]
    public async Task WebhookRespondsWithoutAcknowledgingInvalidOrUnavailableWork(string mode, bool ready,
        string secret, string json, int status)
    {
        using var services = TestServices();
        var controller = WebhookController(services, mode, ready, json, secret);
        Assert.Equal(status, Status(await controller.ReceiveAsync()));
    }

    [Fact]
    public async Task OversizedWebhookRejectedEvenWithChunkedBody()
    {
        using var services = TestServices();
        var controller = WebhookController(services, "Webhook", true, new string(' ', WebhookSecurity.MaxWebhookBytes + 1), "correct-secret");
        Assert.Equal(413, Status(await controller.ReceiveAsync()));
    }

    [Fact]
    public void LivenessAvailableWhileBotIsNotConfigured()
    {
        var controller = new HealthController(new HealthService(new BotRuntimeState(),
            new StartupConfiguration(false, false, false, true, true, false, false, false)));
        Assert.Equal(200, Status(controller.Live()));
        Assert.Equal(200, Status(controller.Configuration()));
        Assert.Equal(503, Status(controller.Ready()));
    }

    [Fact]
    public void PausingSalesKeepsPaymentReceiverReadyForExistingOrders()
    {
        var configuration = new StartupConfiguration(true, true, true, true, true, true, false, true);
        var service = new HealthService(new BotRuntimeState { IsReady = true }, configuration);
        Assert.False(configuration.CanSell);
        Assert.Equal(200, service.Ready().StatusCode);
        Assert.Same(configuration, service.Configuration().Payload);
    }

    [Fact]
    public void ControllersHaveExplicitApiRoutes()
    {
        Assert.NotEmpty(typeof(HealthController).GetCustomAttributes(typeof(ApiControllerAttribute), true));
        Assert.NotEmpty(typeof(TelegramWebhookController).GetCustomAttributes(typeof(ApiControllerAttribute), true));
        Assert.Equal("health", Assert.Single(typeof(HealthController).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Template);
        Assert.Equal("api/telegram", Assert.Single(typeof(TelegramWebhookController).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Template);
        Assert.All(typeof(HealthController).GetConstructors().Single().GetParameters(), parameter => Assert.True(parameter.ParameterType.IsInterface));
        Assert.All(typeof(TelegramWebhookController).GetConstructors().Single().GetParameters(), parameter => Assert.True(parameter.ParameterType.IsInterface));
    }

    [Fact]
    public async Task MissingFinancialHandlerDoesNotAcknowledgeUpdate()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var controller = WebhookController(services, "Webhook", true, "{\"update_id\":42}", "correct-secret");
        Assert.Equal(503, Status(await controller.ReceiveAsync()));
    }

    [Fact]
    public async Task FinancialHandlerFailureReturnsRetryableHttpResponse()
    {
        using var services = new ServiceCollection().AddSingleton<IBotHandler>(new TestHandler { Fail = true }).BuildServiceProvider();
        var controller = WebhookController(services, "Webhook", true, "{\"update_id\":42}", "correct-secret");
        Assert.Equal(503, Status(await controller.ReceiveAsync()));
    }

    [Fact]
    public void HealthControllerReturnsServiceResultWithoutChangingIt()
    {
        var service = new TestHealthService();
        var result = Assert.IsType<ObjectResult>(new HealthController(service).Ready());
        Assert.Equal(418, result.StatusCode);
        Assert.Same(service.Payload, result.Value);
        Assert.Equal(1, service.Calls);
    }

    private static ServiceProvider TestServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBotHandler, TestHandler>();
        return services.BuildServiceProvider();
    }

    private static TelegramWebhookController WebhookController(IServiceProvider services, string mode, bool ready, string json, string secret)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Path = "/api/telegram/webhook";
        context.Response.Body = new MemoryStream();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = secret;
        var service = new TelegramWebhookService(Options.Create(new BotOptions { Mode = mode, WebhookSecret = "correct-secret" }),
            new BotRuntimeState { IsReady = ready }, NullLogger<TelegramWebhookService>.Instance, services.GetService<IBotHandler>());
        return new TelegramWebhookController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static int Status(IActionResult action) => action switch
    {
        StatusCodeResult status => status.StatusCode,
        ObjectResult result => result.StatusCode ?? 200,
        _ => throw new InvalidOperationException("Unexpected controller result.")
    };

    private sealed class TestHandler : IBotHandler
    {
        public bool Fail { get; init; }
        public Task HandleAsync(Update update, CancellationToken ct) => Fail
            ? throw new InvalidOperationException("Durable payment persistence unavailable.") : Task.CompletedTask;
    }

    private sealed class TestHealthService : IHealthService
    {
        public object Payload { get; } = new { result = "from-service" };
        public int Calls { get; private set; }
        public ApiResponse Live() => Ready();
        public ApiResponse Configuration() => Ready();
        public ApiResponse Ready()
        {
            Calls++;
            return new ApiResponse(418, Payload);
        }
    }
}

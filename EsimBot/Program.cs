using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;
using Telegram.Bot;

if (args is ["--setup-database", var host])
{
    try { await DatabaseSetup.RunAsync(host, CancellationToken.None); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Database setup failed ({ex.GetType().Name}). Inspect database and local secret-file existence before retrying.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--export-local-settings"] or ["--export-local-settings", _])
{
    try
    {
        var exportBuilder = WebApplication.CreateBuilder(Array.Empty<string>());
        DatabaseSetup.ExportLocalSettings(args.Length == 2 ? Path.GetFullPath(args[1]) : exportBuilder.Environment.ContentRootPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Local settings export failed ({ex.GetType().Name}).");
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--setup-test-database", var testHost])
{
    try { await DatabaseSetup.RunTestAsync(testHost, CancellationToken.None); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Test database setup failed ({ex.GetType().Name}). Existing databases and settings are never replaced; inspect the setup state before retrying.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--import-test-settings"] or ["--import-test-settings", _])
{
    try { DatabaseSetup.MergeTestSettings(args.Length == 2 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory()); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Test settings import failed ({ex.GetType().Name}). Existing values were not intentionally replaced.");
        Environment.ExitCode = 1;
    }
    return;
}

var diagnosticMode = args.Length == 1 && args[0] is "--check-config" or "--check" or "--verify-db" or "--check-provider" or "--check-stats";
var builder = WebApplication.CreateBuilder(diagnosticMode ? [] : args);
// The owner's filled appsettings.json is copied by normal Build/Publish and excluded from Git.
// Do not load legacy appsettings.Local.json: a leftover server copy must not override the new publication.
builder.Configuration.AddEnvironmentVariables();
if (!diagnosticMode) builder.Configuration.AddCommandLine(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = WebhookSecurity.MaxWebhookBytes);
builder.Services.AddControllersWithViews();
builder.Services.AddOutputCache();
builder.Services.AddSingleton<IHomeService, HomeService>();
builder.Services.AddSingleton<IHealthService, HealthService>();
builder.Services.AddSingleton<ITelegramWebhookService, TelegramWebhookService>();

var botOptions = BotOptions.Resolve(builder.Configuration);
builder.Services.AddSingleton<IOptions<BotOptions>>(Options.Create(botOptions));
var paymentTesting = builder.Configuration.GetSection(PaymentTestOptions.SectionName).Get<PaymentTestOptions>() ?? new();
builder.Services.AddSingleton<IOptions<PaymentTestOptions>>(Options.Create(paymentTesting));
builder.Services.Configure<ProviderOptions>(builder.Configuration.GetSection("Provider"));
builder.Services.Configure<SalesOptions>(builder.Configuration.GetSection("Sales"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<IDatabaseConnectionFactory, DatabaseConnectionFactory>();
builder.Services.AddSingleton<IOrdersRepository, OrdersRepository>();
builder.Services.AddSingleton<IPaymentsRepository, PaymentsRepository>();
builder.Services.AddSingleton<IEsimsRepository, EsimsRepository>();
builder.Services.AddSingleton<IAppStateRepository, AppStateRepository>();
builder.Services.AddSingleton<IOrderService, OrderService>();
builder.Services.AddSingleton<ILanguageService, LanguageService>();
builder.Services.AddSingleton<IPricing, Pricing>();
builder.Services.AddSingleton<IBotRuntimeState, BotRuntimeState>();
builder.Services.AddSingleton<ICatalogService, CatalogService>();
builder.Services.AddHttpClient<IEsimAccessClient, EsimAccessClient>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .RemoveAllLoggers();

var readiness = StartupConfiguration.Inspect(builder.Configuration);
builder.Services.AddSingleton(readiness);
if (readiness.TelegramConfigured)
{
    // Telegram includes its credential in request URLs, so HTTP client logging is disabled at registration.
    builder.Services.AddSingleton<ITelegramRequestGate, TelegramRequestGate>();
    builder.Services.AddTransient<TelegramRateLimitHandler>();
    builder.Services.AddHttpClient("Telegram", client => client.Timeout = TimeSpan.FromSeconds(45))
        .AddHttpMessageHandler<TelegramRateLimitHandler>()
        .RemoveAllLoggers();
    builder.Services.AddSingleton<ITelegramBotClient>(services => new TelegramBotClient(
        new TelegramBotClientOptions(botOptions.Token, useTestEnvironment: botOptions.TestEnvironment)
        { RetryThreshold = 0, RetryCount = 0 },
        services.GetRequiredService<IHttpClientFactory>().CreateClient("Telegram")));
    builder.Services.AddSingleton<IStarRevenueService, StarRevenueService>();
    builder.Services.AddSingleton<IAdminStatisticsService, AdminStatisticsService>();
    builder.Services.AddSingleton<ISupportService, SupportService>();
    builder.Services.AddSingleton<IBotHandler, BotHandler>();
    builder.Services.AddSingleton<IChatUiService, ChatUiService>();
    builder.Services.AddSingleton<IUpdateDispatcher, UpdateDispatcher>();
    if (readiness.ProviderConfigured && readiness.AdministratorConfigured && !readiness.TestMode)
        builder.Services.AddSingleton<IBalanceMonitorService, BalanceMonitorService>();
    builder.Services.AddSingleton<IPaymentTestService, PaymentTestService>();
    builder.Services.AddSingleton<IShopService, ShopService>();
    builder.Services.AddSingleton<IBotNotifier, BotNotifier>();
    builder.Services.AddSingleton<IFulfillment, Fulfillment>();
}
if (readiness.CanStartWorker && !diagnosticMode)
{
    builder.Services.AddSingleton<IEsimBotWorker, EsimBotWorker>();
    builder.Services.AddHostedService(services => services.GetRequiredService<IEsimBotWorker>());
}

var app = builder.Build();
if (diagnosticMode && args[0] == "--check-config")
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(readiness));
    Environment.ExitCode = readiness.CanStartWorker && (readiness.TestMode ? readiness.CanTestPayments : readiness.CanSell) ? 0 : 1;
    await app.DisposeAsync();
    return;
}
if (diagnosticMode)
{
    try
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(readiness));
        if (!readiness.DatabaseConfigured || !readiness.StorageConfigured)
            throw new InvalidOperationException("Database connection or storage encryption settings are incomplete.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var state = app.Services.GetRequiredService<IAppStateRepository>();
        if (args[0] == "--check-stats")
        {
            var statistics = await app.Services.GetRequiredService<IAdminStatisticsService>()
                .GetAsync(botOptions.AdminUserId, timeout.Token)
                ?? throw new InvalidOperationException("Administrator statistics are unavailable.");
            Console.WriteLine(FormattableString.Invariant(
                $"Statistics read-only check: users {statistics.Users}; orders {statistics.Orders.TotalOrders}; payments {statistics.Payments.TotalPayments}; issued eSIMs {statistics.IssuedEsims}."));
            Console.WriteLine("No database row, Telegram message, provider order or webhook setting was changed.");
        }
        else if (args[0] == "--check-provider")
        {
            if (readiness.TestMode) throw new InvalidOperationException("Supplier checks are disabled in payment-test mode.");
            var provider = app.Services.GetRequiredService<IEsimAccessClient>();
            var balance = await provider.GetBalanceUnitsAsync(timeout.Token);
            var packages = await provider.GetPackagesAsync(timeout.Token);
            var countries = packages.SelectMany(package => package.Countries).Select(country => country.Code).Distinct().Count();
            Console.WriteLine(FormattableString.Invariant($"Provider read-only check: balance USD {balance / 10000m:0.0000}; supported fixed-data packages {packages.Count}; covered country codes {countries}."));
            if (readiness.TelegramConfigured)
            {
                var revenue = app.Services.GetRequiredService<IStarRevenueService>();
                await revenue.RefreshAsync(timeout.Token);
                if (!revenue.IsReady) throw new InvalidOperationException("Telegram fee check is unavailable.");
                var sales = app.Services.GetRequiredService<IOptions<SalesOptions>>().Value;
                Console.WriteLine(FormattableString.Invariant($"Telegram read-only fee check: retention {revenue.RevenueRetention:P0}; pricing estimate USD {sales.NetUsdPerStar * revenue.RevenueRetention:0.000000} per Star (not guaranteed payout)."));
            }
            Console.WriteLine("No payment, eSIM purchase, activation or provider webhook change was performed.");
        }
        else if (args[0] == "--verify-db")
        {
            // This diagnostic intentionally verifies only the production app account.
            if (readiness.TestMode) throw new InvalidOperationException("Use --check for the test database; --verify-db is the production-schema diagnostic.");
            await DatabaseDiagnostics.RunAsync(app.Services.GetRequiredService<IDatabaseConnectionFactory>(),
                state, app.Services.GetRequiredService<ISecretProtector>(), timeout.Token);
        }
        else
        {
            await state.VerifyAsync(timeout.Token);
            await state.EnsureEnvironmentAsync(DatabaseEnvironment.Name(builder.Configuration), timeout.Token);
            Console.WriteLine("Database schema verified. This check does not start Telegram or place provider orders.");
            if (!readiness.CanStartWorker || !(readiness.TestMode ? readiness.CanTestPayments : readiness.CanSell)) Environment.ExitCode = 1;
        }
    }
    catch (Exception ex)
    {
        var detail = ex is ProviderException providerError ? $"/{providerError.Code}" : "";
        Console.Error.WriteLine($"Read-only configuration check failed ({ex.GetType().Name}{detail}).");
        Environment.ExitCode = 1;
    }
    await app.DisposeAsync();
    return;
}

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "public,max-age=86400"
});
app.UseOutputCache();
app.MapControllers();
if (!readiness.CanStartWorker)
    app.Logger.LogWarning("Bot configuration is incomplete. HTTP diagnostics are available; Telegram processing is inactive.");
await app.RunAsync();

public partial class Program;

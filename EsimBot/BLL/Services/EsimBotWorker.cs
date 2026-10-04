using System.Globalization;
using EsimBot.BLL.DTO;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EsimBot.BLL.Services;

public sealed class EsimBotWorker(IDatabaseConnectionFactory database, IAppStateRepository state, ITelegramBotClient bot, IUpdateDispatcher dispatcher,
    IFulfillment fulfillment, IBotRuntimeState runtime, IOptions<BotOptions> options,
    ILogger<EsimBotWorker> logger, IStarRevenueService? starRevenue = null,
    IBalanceMonitorService? balanceMonitor = null) : BackgroundService, IEsimBotWorker
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunWithLeaseAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                runtime.IsReady = false;
                logger.LogError("Bot worker unavailable ({ErrorType}); will retry in 30s", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task RunWithLeaseAsync(CancellationToken stoppingToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = run.Token;
        await state.VerifyAsync(ct);
        await using var lease = await database.AcquireWorkerLeaseAsync(ct);
        try
        {
            await state.EnsureEnvironmentAsync(options.Value.TestEnvironment ? "test" : "production", ct);
            var me = await bot.GetMe(ct);
            var botId = me.Id.ToString(CultureInfo.InvariantCulture);
            var savedBotId = await state.GetAsync("telegram_bot_id", ct);
            if (savedBotId is not null && savedBotId != botId)
                throw new InvalidOperationException("This database is already assigned to another Telegram bot.");
            await state.SetAsync("telegram_bot_id", botId, ct);
            if (options.Value.Mode.Equals("Polling", StringComparison.OrdinalIgnoreCase))
            {
                var webhook = await bot.GetWebhookInfo(ct);
                if (!string.IsNullOrWhiteSpace(webhook.Url))
                    throw new InvalidOperationException("Telegram webhook is set. Remove it explicitly before polling.");
            }
            else
            {
                // Explicit Webhook mode registers the configured HTTPS URL, preserving pending updates.
                // Concurrent deliveries prevent a slow catalog request from blocking pre-checkout in another chat.
                await bot.SetWebhook(options.Value.WebhookUrl, maxConnections: 32,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.PreCheckoutQuery],
                    dropPendingUpdates: false, secretToken: options.Value.WebhookSecret, cancellationToken: ct);
            }
            foreach (var language in BotText.SupportedCodes.Append(""))
            {
                var text = new BotText(language.Length == 0 ? options.Value.DefaultLanguage : language);
                var commandNames = options.Value.TestEnvironment
                    ? new[] { "start", "testpayment", "orders", "refund", "id", "language" }
                    : new[] { "start", "orders", "install", "support", "paysupport", "terms", "language" };
                var commands = commandNames.Select(command => new BotCommand
                {
                    Command = command,
                    Description = text.Text((options.Value.TestEnvironment ? "testing.command_" : "ui.command_") + command)
                });
                await bot.SetMyCommands(commands, languageCode: language, cancellationToken: ct);
            }
            if (!options.Value.TestEnvironment && starRevenue is not null)
                await starRevenue.RefreshAsync(ct);
            var tasks = new List<Task> { dispatcher.RunAsync(ct), fulfillment.RunAsync(ct), HeartbeatAsync() };
            if (!options.Value.TestEnvironment && starRevenue is not null)
                tasks.Add(starRevenue.RunAsync(ct));
            if (!options.Value.TestEnvironment && balanceMonitor is not null)
                tasks.Add(balanceMonitor.RunAsync(ct));
            runtime.IsReady = true;
            logger.LogInformation("eSIM bot @{Username} ready in {Mode} mode; payment environment: {Environment}",
                me.Username, options.Value.Mode, options.Value.TestEnvironment ? "TEST (no eSIM purchasing)" : "production");
            if (options.Value.Mode.Equals("Polling", StringComparison.OrdinalIgnoreCase))
                tasks.Add(PollAsync(ct));
            try { await await Task.WhenAny(tasks); }
            finally
            {
                runtime.IsReady = false;
                run.Cancel();
                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            }

            async Task HeartbeatAsync()
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    if (!await lease.PingAsync(ct)) throw new InvalidOperationException("Database worker lease lost.");
                }
            }
        }
        finally
        {
            runtime.IsReady = false;
            run.Cancel();
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var savedOffset = await state.GetAsync("telegram_offset", ct);
        var offset = int.TryParse(savedOffset, out var value) ? value : 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(offset, limit: 50, timeout: 20,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.PreCheckoutQuery], cancellationToken: ct);
                if (updates.Length != 0)
                {
                    await dispatcher.DispatchPollingBatchAsync(updates, ct);
                    var nextOffset = checked(updates.Max(update => update.Id) + 1);
                    // One durable checkpoint per successful batch; never advance past a failed payment write.
                    await state.SetAsync("telegram_offset", nextOffset.ToString(CultureInfo.InvariantCulture), ct);
                    offset = nextOffset;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ApiRequestException ex) when (ex.ErrorCode is 401 or 409)
            { throw new InvalidOperationException("Telegram authentication or polling conflict."); }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(ex.Parameters?.RetryAfter ?? 5, 1, 60)), ct); }
            catch (Exception ex)
            {
                logger.LogWarning("Polling paused ({ErrorType}); update acknowledgement will retry", ex.GetType().Name);
                savedOffset = await state.GetAsync("telegram_offset", ct);
                offset = int.TryParse(savedOffset, out value) ? value : 0;
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

}

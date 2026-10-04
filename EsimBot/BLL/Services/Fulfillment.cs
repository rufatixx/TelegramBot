using EsimBot.BLL.DTO;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace EsimBot.BLL.Services;

public sealed class Fulfillment(IOrderService store, IEsimAccessClient provider, IBotNotifier notifier,
    ITelegramBotClient bot, ILogger<Fulfillment> logger, IOptions<PaymentTestOptions> paymentTesting,
    IBalanceMonitorService? balanceMonitor = null) : IFulfillment
{
    public async Task RunAsync(CancellationToken ct)
    {
        // Refunds have their own loop: supplier outages and slow allocation must never hold them up.
        var refunds = RunRefundsAsync(ct);
        if (paymentTesting.Value.Enabled) { await refunds; return; }
        await Task.WhenAll(refunds, RunOrdersAsync(ct));
    }

    private async Task RunOrdersAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var due = await store.DueOrdersAsync(ct);
                // The worker lease owns this queue. A batch finishes before reading the next one,
                // and each durable order id runs once within the batch.
                await Parallel.ForEachAsync(due.DistinctBy(order => order.Id),
                    new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                    async (order, token) => await ProcessOrderAsync(order, token));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Exception messages/URLs may contain provider keys or activation codes.
                logger.LogError("Fulfillment cycle failed ({ErrorType}); durable work will be retried", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private async Task RunRefundsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessRefundsAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            { logger.LogError("Refund cycle failed ({ErrorType}); durable work will be retried", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private async Task ProcessOrderAsync(Order order, CancellationToken ct)
    {
        if (paymentTesting.Value.Enabled) return;
        // A copied or previously-created test order can never become a live eSIM, even after switching modes.
        if (string.Equals(order.PackageCode, PaymentTestOptions.PackageCode, StringComparison.Ordinal))
        {
            // Existing paid/ready states intentionally cannot be forced to manual_review by the retry operation.
            // Defer them for one day, keep the blocking code durable, and leave resolution/refund to an operator.
            // This guard always runs before profile reads and purchase calls, including on the next retry.
            await store.RetryOrderAsync(order.Id, "reserved_test_package", 86400, true, ct);
            logger.LogWarning("Order {OrderId} blocked for operator review (reserved_test_package)", order.Id);
            if (order.Attempts != 0) return;
            try { await notifier.AlertAdminAsync(order.Id, "reserved_test_package", ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning("Test order quarantine alert failed ({ErrorType})", ex.GetType().Name); }
            return;
        }
        try
        {
            if (order.State == "ready")
            {
                var profile = await store.GetProfileAsync(order.Id, order.UserId, ct)
                    ?? throw new InvalidOperationException("Missing profile.");
                await notifier.DeliverAsync(order, profile, ct);
                await store.MarkDeliveredAsync(order.Id, ct);
                return;
            }
            if (!provider.IsConfigured) return;
            if (order.State == "paid")
            {
                if (balanceMonitor is not null)
                {
                    var balance = await provider.GetBalanceUnitsAsync(ct);
                    if (balance < order.CostUnits)
                    {
                        balanceMonitor.ReportInsufficient(order.CostUnits, balance);
                        await store.RetryOrderAsync(order.Id, "supplier_balance_low", 30, false, ct);
                        return;
                    }
                }
                if (!await store.BeginProvisionAsync(order.Id, ct)) return;
            }
            // Provisioning retries must reconcile even with a zero balance: the original purchase
            // may already have charged the supplier account before its response was interrupted.
            var orderNumber = order.ProviderOrderNumber;
            if (orderNumber is null)
            {
                // Reuse the same transaction ID after any timeout, cancellation or process restart.
                orderNumber = await provider.PlaceOrderAsync(order.Id, order.PackageCode, order.CostUnits, ct);
                await store.SaveProviderOrderAsync(order.Id, orderNumber, ct);
            }
            var allocation = await provider.QueryOrderAsync(order.Id, orderNumber, ct);
            if (allocation is null || allocation.Profiles.Count == 0)
            {
                await RetryAsync(order, "allocation_pending", ct);
                return;
            }
            if (allocation.OrderNumber != orderNumber || allocation.Profiles.Count != 1)
            {
                await store.RetryOrderAsync(order.Id, "allocation_mismatch", 300, true, ct);
                await notifier.AlertAdminAsync(order.Id, "allocation_mismatch", ct);
                return;
            }
            await store.SaveProfileAsync(order.Id, allocation.Profiles[0], ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var code = ex is ProviderException pe ? pe.Code : ex.GetType().Name;
            logger.LogWarning("Order {OrderId} deferred ({Code})", order.Id, code);
            await RetryAsync(order, code, ct);
        }
    }

    private async Task RetryAsync(Order order, string code, CancellationToken ct)
    {
        var manual = order.Attempts >= 11 && order.State != "ready";
        var delay = Math.Min(300, 5 * (1 << Math.Min(order.Attempts, 6)));
        await store.RetryOrderAsync(order.Id, code, delay, manual, ct);
        if (manual || order.Attempts is 3 or 11)
        {
            try { await notifier.AlertAdminAsync(order.Id, code, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning("Admin notification failed ({ErrorType})", ex.GetType().Name); }
        }
    }

    private async Task ProcessRefundsAsync(CancellationToken ct)
    {
        foreach (var payment in await store.DueRefundsAsync(ct))
        {
            try
            {
                try { await bot.RefundStarPayment(payment.UserId, payment.ChargeId, ct); }
                // Telegram may have refunded before a process crash prevented the DB commit.
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("CHARGE_ALREADY_REFUNDED", StringComparison.OrdinalIgnoreCase)) { }
                await store.CompleteRefundAsync(payment, ct);
                if (payment.OrderId is not null)
                {
                    var order = await store.GetAsync(payment.OrderId, payment.UserId, ct);
                    if (order is { State: "refunded" })
                    {
                        try { await notifier.RefundedAsync(order, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        { logger.LogWarning("Refund receipt notification failed ({ErrorType})", ex.GetType().Name); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError("Refund deferred ({ErrorType})", ex.GetType().Name);
                await store.RetryRefundAsync(payment.ChargeId, ct);
                try { await notifier.AlertAdminAsync(payment.OrderId ?? "unmatched-payment", "refund_pending", ct); }
                catch (Exception notifyEx) when (notifyEx is not OperationCanceledException)
                { logger.LogWarning("Refund alert failed ({ErrorType})", notifyEx.GetType().Name); }
            }
        }
    }
}

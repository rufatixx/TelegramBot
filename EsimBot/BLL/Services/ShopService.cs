using EsimBot.BLL.DTO;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;

namespace EsimBot.BLL.Services;

public sealed class ShopService(
    IEsimAccessClient provider, IOrderService store, IPricing pricing,
    IOptions<SalesOptions> salesOptions, IOptions<BotOptions> botOptions, IBotNotifier notifier,
    ILogger<ShopService> logger, IStarRevenueService? revenue = null,
    ICatalogService? catalog = null, IBalanceMonitorService? balanceMonitor = null) : IShopService
{
    public ShopReadiness Readiness => new(provider.IsConfigured,
        provider.IsConfigured && salesOptions.Value.IsConfigured && botOptions.Value.AdminUserId > 0
        && (revenue?.IsReady ?? true));

    public Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct) =>
        catalog?.GetPackagesAsync(ct) ?? provider.GetPackagesAsync(ct);

    public async Task<Order> QuoteAsync(long userId, string packageCode, string requestKey, CancellationToken ct)
    {
        if (!Readiness.CanBuy) throw new ShopException("Продажи ещё не открыты. Попробуйте позже.");
        var package = (await GetPackagesAsync(ct)).SingleOrDefault(p => p.Code == packageCode)
            ?? throw new ShopException("Тариф больше недоступен. Выберите другой.");
        var stars = pricing.StarsFor(package);
        if (stars > 100000) throw new ShopException("Тариф пока недоступен для оплаты.");
        return await store.CreateQuoteAsync(userId, package, stars, salesOptions.Value.TermsVersion, requestKey, ct);
    }

    public async Task<string?> ValidateCheckoutAsync(long userId, string orderId, string currency, int stars, CancellationToken ct)
    {
        if (!Readiness.CanBuy) return "ui.checkout_paused";
        var order = await store.GetAsync(orderId, userId, ct);
        if (order is null || order.State != "quoted") return "ui.checkout_order_unavailable";
        if (currency != "XTR" || order.Stars != stars) return "ui.checkout_price_changed";
        if (DateTime.UtcNow >= DateTime.SpecifyKind(order.ExpiresAt, DateTimeKind.Utc)) return "ui.checkout_expired";
        // A change in Telegram fees must not make an old invoice sell below the configured margin.
        if (!pricing.CoversQuote(order.CostUnits, order.Stars)) return "ui.checkout_price_changed";
        var balance = await provider.GetBalanceUnitsAsync(ct);
        if (balance < order.CostUnits)
        {
            balanceMonitor?.ReportInsufficient(order.CostUnits, balance);
            return "ui.checkout_no_balance";
        }
        // The wallet lookup is network I/O; a fee refresh or expiry may happen while it is in flight.
        if (!Readiness.CanBuy) return "ui.checkout_paused";
        if (!pricing.CoversQuote(order.CostUnits, order.Stars)) return "ui.checkout_price_changed";
        return null;
    }

    public Task<PaymentResult> RecordPaymentAsync(long userId, string orderId, string chargeId, string currency, int stars, CancellationToken ct)
    {
        if (currency != "XTR") throw new InvalidOperationException("Unexpected payment currency; reconciliation required.");
        // A confirmed payment is recorded even if sales were disabled or its quote has since expired.
        return store.RecordPaymentAsync(userId, orderId, chargeId, stars, ct);
    }

    public async Task RecordRefundAsync(long userId, string chargeId, CancellationToken ct)
    {
        var needsReconciliation = await store.RecordExternalRefundAsync(userId, chargeId, ct);
        if (needsReconciliation is not null)
        {
            try { await notifier.AlertAdminAsync(needsReconciliation, "refund_after_supplier_submission", ct); }
            catch (Exception ex)
            {
                // The refund is already committed. A failed receipt must not block acknowledgement.
                logger.LogWarning("Refund saved but admin alert failed ({ExceptionType})", ex.GetType().Name);
            }
        }
    }

    public Task<IReadOnlyList<Order>> GetOrdersAsync(long userId, int page, CancellationToken ct) => store.ListAsync(userId, page, ct);
    public Task<Order?> GetOrderAsync(long userId, string orderId, CancellationToken ct) => store.GetAsync(orderId, userId, ct);
    public Task<StoredProfile?> GetProfileAsync(long userId, string orderId, CancellationToken ct) => store.GetProfileAsync(orderId, userId, ct);

    public async Task<EsimUsage?> GetUsageAsync(long userId, string orderId, CancellationToken ct)
    {
        var profile = await store.GetProfileAsync(orderId, userId, ct);
        return profile is null ? null : await provider.GetUsageAsync(profile.Id, ct);
    }

    public Task<bool> RequestRefundAsync(long adminUserId, string orderId, CancellationToken ct)
    {
        if (botOptions.Value.AdminUserId <= 0 || botOptions.Value.AdminUserId != adminUserId) return Task.FromResult(false);
        return store.QueueUnsubmittedRefundAsync(orderId, ct);
    }

    public Task<bool> RequestRetryAsync(long adminUserId, string orderId, CancellationToken ct)
    {
        if (botOptions.Value.AdminUserId <= 0 || botOptions.Value.AdminUserId != adminUserId) return Task.FromResult(false);
        return store.ResumeProvisionAsync(orderId, ct);
    }
}

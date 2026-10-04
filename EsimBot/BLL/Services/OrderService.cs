using EsimBot.BLL.DTO;
using EsimBot.DAL.DAO;
using EsimBot.DAL.Repos;

namespace EsimBot.BLL.Services;

/// <summary>Business rules and atomic workflows across orders, payments and eSIM repositories.</summary>
public sealed class OrderService(
    IDatabaseConnectionFactory database, IOrdersRepository orders, IPaymentsRepository payments,
    IEsimsRepository esims, ISecretProtector protector) : IOrderService
{
    public async Task<Order> CreateQuoteAsync(long userId, EsimPackage package, int stars, string termsVersion, string requestKey, CancellationToken ct)
    {
        if (userId <= 0 || requestKey.Length is < 1 or > 96 || termsVersion.Length is < 1 or > 64)
            throw new ArgumentException("Invalid quote identity or terms version.");
        await using var session = await database.OpenSessionAsync(ct);
        await orders.InsertQuoteAsync(session, new OrderQuoteDao(Guid.NewGuid().ToString("N"), userId,
            requestKey, package.Code, package.Name, package.PriceUnits, stars, termsVersion, DateTime.UtcNow.AddMinutes(15)), ct);
        var order = await orders.FindByRequestKeyAsync(session, requestKey, ct)
            ?? throw new InvalidOperationException("Created quote was not found.");
        if (order.UserId != userId || order.PackageCode != package.Code)
            throw new InvalidOperationException("Quote request identity conflict.");
        return ToDto(order);
    }

    public async Task<Order?> GetAsync(string id, long? userId, CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        var order = await orders.GetAsync(session, id, userId, false, ct);
        return order is null ? null : ToDto(order);
    }

    public async Task<IReadOnlyList<Order>> ListAsync(long userId, int page, CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        return (await orders.ListAsync(session, userId, Math.Clamp(page, 0, 10000) * 8, ct)).Select(ToDto).ToArray();
    }

    public async Task<PaymentResult> RecordPaymentAsync(long payerId, string orderId, string chargeId, int stars, CancellationToken ct)
    {
        if (payerId <= 0 || stars <= 0 || chargeId.Length is < 1 or > 255) throw new ArgumentException("Invalid payment.");
        await using var session = await database.BeginTransactionAsync(ct);
        // All payment mutations acquire the order before its payment to keep lock ordering consistent.
        var order = await orders.GetAsync(session, orderId, null, true, ct);
        var existing = await payments.GetAsync(session, chargeId, false, ct);
        if (existing is not null)
        {
            if (existing.PayerId != payerId || existing.Stars != stars || existing.OrderId != order?.Id)
                throw new InvalidOperationException("Payment identity conflict.");
            await session.CommitAsync(ct);
            return new(existing.State == "accepted", true, existing.State == "refund_pending");
        }
        var accepted = order is { State: "quoted" } && order.UserId == payerId && order.Stars == stars;
        var now = DateTime.UtcNow;
        await payments.InsertAsync(session, new PaymentDao(chargeId, order?.Id, payerId, stars,
            accepted ? "accepted" : "refund_pending", accepted ? now : null, now, null, 0, now), ct);
        if (accepted) await orders.SetPaidAsync(session, order!.Id, ct);
        await session.CommitAsync(ct);
        return new(accepted, false, !accepted);
    }

    public async Task<StoredProfile?> GetProfileAsync(string orderId, long userId, CancellationToken ct)
    {
        await using var session = await database.BeginTransactionAsync(ct);
        var order = await orders.GetAsync(session, orderId, userId, true, ct);
        if (order is not { State: "ready" }) return null;
        var profile = await esims.GetAsync(session, orderId, ct);
        var result = profile is null ? null : ToDto(profile);
        await session.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<Order>> DueOrdersAsync(CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        return (await orders.DueAsync(session, ct)).Select(ToDto).ToArray();
    }

    public async Task<bool> BeginProvisionAsync(string id, CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        return await orders.BeginProvisionAsync(session, id, ct);
    }

    public async Task SaveProviderOrderAsync(string id, string orderNumber, CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        if (!await orders.SaveProviderOrderAsync(session, id, orderNumber, ct))
            throw new InvalidOperationException("Provider order association conflict.");
    }

    public async Task SaveProfileAsync(string orderId, ProviderProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Iccid)
            || !profile.ActivationCode.StartsWith("LPA:1$", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid eSIM profile; manual reconciliation required.");
        await using var session = await database.BeginTransactionAsync(ct);
        var order = await orders.GetAsync(session, orderId, null, true, ct)
            ?? throw new InvalidOperationException("Order not found.");
        if (order.State == "ready") { await session.CommitAsync(ct); return; }
        if (order.State != "provisioning") throw new InvalidOperationException("Invalid provisioning state.");
        await esims.InsertAsync(session, new ProfileDao(orderId, profile.Id, profile.Iccid,
            protector.Encrypt(orderId, profile.ActivationCode), profile.Apn), ct);
        await orders.MarkReadyAsync(session, orderId, ct);
        await session.CommitAsync(ct);
    }

    public async Task MarkDeliveredAsync(string id, CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        await orders.MarkDeliveredAsync(session, id, ct);
    }

    public async Task RetryOrderAsync(string id, string code, int delaySeconds, bool manualReview, CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        await orders.RetryAsync(session, id, code[..Math.Min(code.Length, 80)], delaySeconds, manualReview, ct);
    }

    public async Task<bool> QueueUnsubmittedRefundAsync(string id, CancellationToken ct)
    {
        await using var session = await database.BeginTransactionAsync(ct);
        var order = await orders.GetAsync(session, id, null, true, ct);
        // A submitted or ambiguous supplier outcome must be reconciled before any refund.
        if (order?.State != "paid") return false;
        await orders.QueueRefundAsync(session, id, ct);
        await payments.QueuePrimaryRefundAsync(session, id, ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<Payment>> DueRefundsAsync(CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        return (await payments.DueRefundsAsync(session, ct)).Select(ToDto).ToArray();
    }

    public async Task CompleteRefundAsync(Payment payment, CancellationToken ct)
    {
        await using var session = await database.BeginTransactionAsync(ct);
        if (payment.OrderId is not null) await orders.GetAsync(session, payment.OrderId, null, true, ct);
        var stored = await payments.GetAsync(session, payment.ChargeId, true, ct);
        if (stored is null || stored.PayerId != payment.UserId || stored.OrderId != payment.OrderId || stored.Stars != payment.Stars)
            throw new InvalidOperationException("Refund payment identity conflict.");
        await payments.MarkRefundedAsync(session, stored.ChargeId, ct);
        if (stored.AcceptedAt is not null && stored.OrderId is not null)
            await orders.MarkRefundedAsync(session, stored.OrderId, true, ct);
        await session.CommitAsync(ct);
    }

    public async Task RetryRefundAsync(string chargeId, CancellationToken ct)
    {
        await using var session = await database.OpenSessionAsync(ct);
        await payments.RetryRefundAsync(session, chargeId, ct);
    }

    public async Task<bool> ResumeProvisionAsync(string id, CancellationToken ct)
    {
        await using var session = await database.BeginTransactionAsync(ct);
        var order = await orders.GetAsync(session, id, null, true, ct);
        if (order?.State != "manual_review") return false;
        var payment = await payments.FindPrimaryForOrderAsync(session, id, ct);
        if (payment?.State != "accepted") return false;
        await orders.ResumeProvisionAsync(session, id, ct);
        await session.CommitAsync(ct);
        return true;
    }

    public async Task<string?> RecordExternalRefundAsync(long userId, string chargeId, CancellationToken ct)
    {
        await using var session = await database.BeginTransactionAsync(ct);
        var payment = await payments.GetAsync(session, chargeId, false, ct);
        if (payment is null || payment.PayerId != userId)
            throw new InvalidOperationException("Refund payment not found or payer mismatch.");
        var order = payment.OrderId is null ? null : await orders.GetAsync(session, payment.OrderId, null, true, ct);
        payment = await payments.GetAsync(session, chargeId, true, ct)
            ?? throw new InvalidOperationException("Refund payment disappeared.");
        // accepted_at is permanent: refunding a secondary charge never cancels the real purchase.
        var affectsOrder = payment.AcceptedAt is not null;
        await payments.MarkRefundedAsync(session, chargeId, ct);
        if (affectsOrder && order is not null) await orders.MarkRefundedAsync(session, order.Id, false, ct);
        await session.CommitAsync(ct);
        return affectsOrder && order?.State is "provisioning" or "ready" or "manual_review" ? order.Id : null;
    }

    private static Order ToDto(OrderDao row) => new(row.Id, row.UserId, row.PackageCode, row.PackageName,
        row.CostUnits, row.Stars, row.State, row.ProviderOrderNumber, row.CreatedAt, row.ExpiresAt,
        row.Attempts, row.DeliveredAt);

    private static Payment ToDto(PaymentDao row) => new(row.ChargeId, row.OrderId, row.PayerId,
        row.Stars, row.State, row.AcceptedAt);

    private StoredProfile ToDto(ProfileDao row) => new(row.ProviderEsimId, row.Iccid,
        protector.Decrypt(row.OrderId, row.ActivationCiphertext), row.Apn);
}

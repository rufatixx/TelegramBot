using Dapper;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.DAL.DAO;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace EsimBot.Tests;

/// <summary>
/// Deliberately opt-in checks of real MySQL transactions. Each fixture tracks exact synthetic
/// identifiers before issuing writes, removes only those rows in finally, and verifies cleanup.
/// No test starts a bot, contacts Telegram, or calls an eSIM provider.
/// </summary>
public sealed class StoreIntegrationTests
{
    [DatabaseFact]
    public async Task Statistics_aggregates_map_to_integer_DAOs_on_real_MySQL()
    {
        await using var fixture = new StoreFixture();
        await using var session = await fixture.Database.OpenSessionAsync(fixture.Token);
        var orderStats = await new OrdersRepository().GetStatisticsAsync(session, fixture.Token);
        var paymentStats = await new PaymentsRepository().GetStatisticsAsync(session, fixture.Token);
        var issued = await new EsimsRepository().CountAsync(session, fixture.Token);

        Assert.True(orderStats.TotalOrders >= 0);
        Assert.True(orderStats.Quoted >= 0);
        Assert.True(paymentStats.TotalPayments >= 0);
        Assert.True(paymentStats.GrossStars >= 0);
        Assert.True(issued >= 0);
    }

    [DatabaseFact]
    public async Task A_failed_later_repository_write_rolls_back_both_order_and_payment()
    {
        await using var fixture = new StoreFixture();
        var id = Guid.NewGuid().ToString("N");
        var requestKey = fixture.RequestKey();
        var charge = fixture.ChargeId();
        var invalidCharge = fixture.ChargeId();
        var now = DateTime.UtcNow;
        await using (var session = await fixture.Database.BeginTransactionAsync(fixture.Token))
        {
            IOrdersRepository orders = new OrdersRepository();
            IPaymentsRepository payments = new PaymentsRepository();
            await orders.InsertQuoteAsync(session, new OrderQuoteDao(id, fixture.UserId, requestKey,
                "test-package", "Atomic rollback test", 10000, 100, "integration-test", now.AddMinutes(15)), fixture.Token);
            await payments.InsertAsync(session, new PaymentDao(charge, id, fixture.UserId, 100,
                "accepted", now, now, null, 0, now), fixture.Token);
            var failure = await Assert.ThrowsAsync<MySqlException>(() => payments.InsertAsync(session,
                new PaymentDao(invalidCharge, id, fixture.UserId, 0, "refund_pending", null, now, null, 0, now), fixture.Token));
            Assert.Equal(3819, failure.Number);
            // No commit: disposal must roll back every preceding repository write in this session.
        }
        Assert.Null(await fixture.Store.GetAsync(id, fixture.UserId, fixture.Token));
        await using var db = await fixture.Database.OpenAsync(fixture.Token);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payments WHERE charge_id IN @Charges",
            new { Charges = new[] { charge, invalidCharge } }));
    }

    [DatabaseFact]
    public async Task Worker_leases_are_exclusive_and_release_their_dedicated_session()
    {
        await using var fixture = new StoreFixture();
        await using var first = await fixture.Database.AcquireWorkerLeaseAsync(fixture.Token);
        Assert.True(await first.PingAsync(fixture.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Database.AcquireWorkerLeaseAsync(fixture.Token));
        await first.DisposeAsync();
        Assert.False(await first.PingAsync(fixture.Token));
        await using var second = await fixture.Database.AcquireWorkerLeaseAsync(fixture.Token);
        Assert.True(await second.PingAsync(fixture.Token));
    }

    [DatabaseFact]
    public async Task Repeated_callback_returns_the_original_quote_without_changing_its_price()
    {
        await using var fixture = new StoreFixture();
        var requestKey = fixture.RequestKey();
        var original = await fixture.QuoteAsync(requestKey: requestKey);
        var replay = await fixture.Store.CreateQuoteAsync(fixture.UserId,
            fixture.Package with { Name = "Changed upstream description", PriceUnits = 20000 },
            300, "new-terms", requestKey, fixture.Token);

        Assert.Equal(original, replay);
        Assert.Equal("quoted", replay.State);
        Assert.Equal(10000, replay.CostUnits);
        Assert.Equal(100, replay.Stars);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.CreateQuoteAsync(
            fixture.OtherUserId, fixture.Package, 100, "test-terms", requestKey, fixture.Token));
        await using var db = await fixture.Database.OpenAsync(fixture.Token);
        Assert.Equal("+00:00", await db.ExecuteScalarAsync<string>("SELECT @@session.time_zone"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM orders WHERE request_key=@RequestKey", new { RequestKey = requestKey }));
    }

    [DatabaseFact]
    public async Task Concurrent_delivery_of_one_charge_creates_one_accepted_payment()
    {
        await using var fixture = new StoreFixture();
        var order = await fixture.QuoteAsync();
        var charge = fixture.ChargeId();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            fixture.Store.RecordPaymentAsync(fixture.UserId, order.Id, charge, order.Stars, fixture.Token)));

        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.Single(results, result => !result.Duplicate);
        Assert.All(results, result => Assert.False(result.RefundQueued));
        Assert.Equal("paid", (await fixture.OrderAsync(order.Id)).State);
        await using var db = await fixture.Database.OpenAsync(fixture.Token);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payments WHERE order_id=@Id", new { order.Id }));
        Assert.Equal("accepted", (await fixture.PaymentAsync(charge)).State);
    }

    [DatabaseFact]
    public async Task A_second_charge_is_refunded_without_cancelling_the_real_purchase()
    {
        await using var fixture = new StoreFixture();
        var order = await fixture.QuoteAsync();
        var primary = await fixture.PayAsync(order);
        var duplicate = fixture.ChargeId();
        var result = await fixture.Store.RecordPaymentAsync(fixture.UserId, order.Id, duplicate, order.Stars, fixture.Token);

        Assert.False(result.Accepted);
        Assert.True(result.RefundQueued);
        Assert.False(result.Duplicate);
        var replay = await fixture.Store.RecordPaymentAsync(fixture.UserId, order.Id, duplicate, order.Stars, fixture.Token);
        Assert.True(replay.Duplicate);
        Assert.True(replay.RefundQueued);
        Assert.Null(await fixture.Store.RecordExternalRefundAsync(fixture.UserId, duplicate, fixture.Token));
        Assert.Equal("refunded", (await fixture.PaymentAsync(duplicate)).State);
        Assert.Equal("accepted", (await fixture.PaymentAsync(primary)).State);
        Assert.Equal("paid", (await fixture.OrderAsync(order.Id)).State);

        var otherOrder = await fixture.QuoteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.RecordPaymentAsync(
            fixture.UserId, otherOrder.Id, primary, order.Stars, fixture.Token));
        Assert.Equal("quoted", (await fixture.OrderAsync(otherOrder.Id)).State);
    }

    [DatabaseFact]
    public async Task Wrong_payer_or_amount_is_refunded_without_activating_the_order()
    {
        await using var fixture = new StoreFixture();
        foreach (var wrongPayer in new[] { true, false })
        {
            var order = await fixture.QuoteAsync();
            var payer = wrongPayer ? fixture.OtherUserId : fixture.UserId;
            var amount = wrongPayer ? order.Stars : order.Stars + 1;
            var charge = fixture.ChargeId();
            var result = await fixture.Store.RecordPaymentAsync(payer, order.Id, charge, amount, fixture.Token);

            Assert.False(result.Accepted);
            Assert.True(result.RefundQueued);
            Assert.Equal("quoted", (await fixture.OrderAsync(order.Id)).State);
            Assert.Equal("refund_pending", (await fixture.PaymentAsync(charge)).State);
            Assert.False(await fixture.Store.BeginProvisionAsync(order.Id, fixture.Token));
            Assert.Null(await fixture.Store.RecordExternalRefundAsync(payer, charge, fixture.Token));
            Assert.Equal("quoted", (await fixture.OrderAsync(order.Id)).State);
        }
    }

    [DatabaseFact]
    public async Task Profiles_are_encrypted_owner_bound_and_cannot_be_allocated_to_two_orders()
    {
        await using var fixture = new StoreFixture();
        var first = await fixture.ProvisioningOrderAsync();
        var second = await fixture.ProvisioningOrderAsync();
        var profile = fixture.Profile();
        await fixture.Store.SaveProfileAsync(first.Id, profile, fixture.Token);
        await fixture.Store.SaveProfileAsync(first.Id, profile, fixture.Token);

        Assert.Equal("ready", (await fixture.OrderAsync(first.Id)).State);
        Assert.Equal(profile.ActivationCode, (await fixture.Store.GetProfileAsync(first.Id, fixture.UserId, fixture.Token))!.ActivationCode);
        Assert.Null(await fixture.Store.GetAsync(first.Id, fixture.OtherUserId, fixture.Token));
        Assert.Null(await fixture.Store.GetProfileAsync(first.Id, fixture.OtherUserId, fixture.Token));
        await using (var db = await fixture.Database.OpenAsync(fixture.Token))
        {
            var encrypted = await db.ExecuteScalarAsync<string>("SELECT activation_ciphertext FROM esims WHERE order_id=@Id", new { first.Id });
            Assert.StartsWith("v1:", encrypted);
            Assert.DoesNotContain(profile.ActivationCode, encrypted);
        }

        var conflict = await Assert.ThrowsAsync<MySqlException>(() => fixture.Store.SaveProfileAsync(second.Id, profile, fixture.Token));
        Assert.Equal(1062, conflict.Number);
        Assert.Equal("provisioning", (await fixture.OrderAsync(second.Id)).State);
        Assert.Null(await fixture.Store.GetProfileAsync(second.Id, fixture.UserId, fixture.Token));
        await fixture.Store.MarkDeliveredAsync(first.Id, fixture.Token);
        Assert.NotNull((await fixture.OrderAsync(first.Id)).DeliveredAt);
    }

    [DatabaseFact]
    public async Task Unsubmitted_refunds_and_manual_review_resume_preserve_payment_state()
    {
        await using var fixture = new StoreFixture();
        var order = await fixture.QuoteAsync();
        Assert.False(await fixture.Store.QueueUnsubmittedRefundAsync(order.Id, fixture.Token));
        var charge = await fixture.PayAsync(order);
        Assert.True(await fixture.Store.QueueUnsubmittedRefundAsync(order.Id, fixture.Token));
        Assert.Equal("refund_pending", (await fixture.OrderAsync(order.Id)).State);
        Assert.Equal("refund_pending", (await fixture.PaymentAsync(charge)).State);
        Assert.False(await fixture.Store.BeginProvisionAsync(order.Id, fixture.Token));
        await fixture.Store.CompleteRefundAsync(await fixture.PaymentAsync(charge), fixture.Token);
        Assert.Equal("refunded", (await fixture.OrderAsync(order.Id)).State);
        Assert.Equal("refunded", (await fixture.PaymentAsync(charge)).State);
        Assert.False(await fixture.Store.ResumeProvisionAsync(order.Id, fixture.Token));

        var pending = await fixture.ProvisioningOrderAsync();
        var providerNumber = (await fixture.OrderAsync(pending.Id)).ProviderOrderNumber;
        await fixture.Store.SaveProviderOrderAsync(pending.Id, providerNumber!, fixture.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveProviderOrderAsync(
            pending.Id, "conflicting-" + Guid.NewGuid().ToString("N"), fixture.Token));
        await fixture.Store.RetryOrderAsync(pending.Id, "test_timeout", 300, true, fixture.Token);
        Assert.Equal("manual_review", (await fixture.OrderAsync(pending.Id)).State);
        Assert.False(await fixture.Store.QueueUnsubmittedRefundAsync(pending.Id, fixture.Token));
        Assert.True(await fixture.Store.ResumeProvisionAsync(pending.Id, fixture.Token));
        var resumed = await fixture.OrderAsync(pending.Id);
        Assert.Equal("provisioning", resumed.State);
        Assert.Equal(0, resumed.Attempts);
        Assert.Equal(providerNumber, resumed.ProviderOrderNumber);
        Assert.False(await fixture.Store.ResumeProvisionAsync(pending.Id, fixture.Token));
    }

    [DatabaseFact]
    public async Task External_refunds_require_the_charge_owner_and_hide_already_issued_profiles()
    {
        await using var fixture = new StoreFixture();
        var order = await fixture.QuoteAsync();
        var charge = await fixture.PayAsync(order);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.RecordExternalRefundAsync(
            fixture.OtherUserId, charge, fixture.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.RecordExternalRefundAsync(
            fixture.UserId, fixture.ChargeId(), fixture.Token));
        Assert.Equal("paid", (await fixture.OrderAsync(order.Id)).State);
        Assert.Equal("accepted", (await fixture.PaymentAsync(charge)).State);

        Assert.True(await fixture.Store.BeginProvisionAsync(order.Id, fixture.Token));
        await fixture.Store.SaveProviderOrderAsync(order.Id, "test-provider-" + Guid.NewGuid().ToString("N"), fixture.Token);
        await fixture.Store.SaveProfileAsync(order.Id, fixture.Profile(), fixture.Token);
        Assert.Equal(order.Id, await fixture.Store.RecordExternalRefundAsync(fixture.UserId, charge, fixture.Token));
        Assert.Equal("refunded", (await fixture.OrderAsync(order.Id)).State);
        Assert.Null(await fixture.Store.GetProfileAsync(order.Id, fixture.UserId, fixture.Token));
        Assert.Null(await fixture.Store.RecordExternalRefundAsync(fixture.UserId, charge, fixture.Token));
        Assert.False(await fixture.Store.ResumeProvisionAsync(order.Id, fixture.Token));
    }

    [DatabaseFact]
    public Task Completing_a_secondary_refund_does_not_finish_an_outstanding_primary_refund()
        => AssertSecondaryRefundDoesNotFinishPrimaryAsync(externalRefund: false);

    [DatabaseFact]
    public Task An_external_secondary_refund_does_not_finish_an_outstanding_primary_refund()
        => AssertSecondaryRefundDoesNotFinishPrimaryAsync(externalRefund: true);

    private static async Task AssertSecondaryRefundDoesNotFinishPrimaryAsync(bool externalRefund)
    {
        await using var fixture = new StoreFixture();
        var order = await fixture.QuoteAsync();
        var primary = await fixture.PayAsync(order);
        var secondary = fixture.ChargeId();
        await fixture.Store.RecordPaymentAsync(fixture.UserId, order.Id, secondary, order.Stars, fixture.Token);
        Assert.True(await fixture.Store.QueueUnsubmittedRefundAsync(order.Id, fixture.Token));
        if (externalRefund)
            await fixture.Store.RecordExternalRefundAsync(fixture.UserId, secondary, fixture.Token);
        else
            await fixture.Store.CompleteRefundAsync(await fixture.PaymentAsync(secondary), fixture.Token);

        Assert.Equal("refunded", (await fixture.PaymentAsync(secondary)).State);
        Assert.Null((await fixture.PaymentAsync(secondary)).AcceptedAt);
        Assert.Equal("refund_pending", (await fixture.PaymentAsync(primary)).State);
        Assert.NotNull((await fixture.PaymentAsync(primary)).AcceptedAt);
        Assert.Equal("refund_pending", (await fixture.OrderAsync(order.Id)).State);
        await fixture.Store.CompleteRefundAsync(await fixture.PaymentAsync(primary), fixture.Token);
        Assert.Equal("refunded", (await fixture.OrderAsync(order.Id)).State);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly HashSet<string> _requestKeys = [];
        private readonly HashSet<string> _chargeIds = [];
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(90));
        public long UserId { get; } = Random.Shared.NextInt64(10_000_000_000, 1_000_000_000_000);
        public long OtherUserId => UserId + 1;
        public CancellationToken Token => _timeout.Token;
        public IOrderService Store { get; }
        public IDatabaseConnectionFactory Database { get; }
        public EsimPackage Package { get; } = new("test-package", "Rollback-cleaned integration test", 10000,
            "USD", 1024L * 1024 * 1024, 7, "DAY", [new Country("AZ", "Azerbaijan")], "", "", "", "");

        public StoreFixture()
        {
            var path = Path.GetFullPath(Environment.GetEnvironmentVariable("ESIMBOT_INTEGRATION_CONFIG")
                ?? throw new InvalidOperationException("Explicit integration configuration is required."));
            var configuration = new ConfigurationBuilder().SetBasePath(Path.GetDirectoryName(path)!)
                .AddJsonFile(Path.GetFileName(path), optional: false)
                // Integration writes are always isolated from the production database and worker.
                .AddInMemoryCollection(new Dictionary<string, string?> { ["PaymentTesting:Enabled"] = "true" })
                .Build();
            var storage = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
            Database = new DatabaseConnectionFactory(configuration);
            Store = new OrderService(Database, new OrdersRepository(), new PaymentsRepository(),
                new EsimsRepository(), new SecretProtector(Options.Create(storage)));
        }

        public string RequestKey()
        {
            var key = "integration-" + Guid.NewGuid().ToString("N");
            _requestKeys.Add(key);
            return key;
        }

        public string ChargeId()
        {
            var charge = "integration-" + Guid.NewGuid().ToString("N");
            _chargeIds.Add(charge);
            return charge;
        }

        public Task<Order> QuoteAsync(string? requestKey = null)
            => Store.CreateQuoteAsync(UserId, Package, 100, "integration-test", requestKey ?? RequestKey(), Token);

        public async Task<string> PayAsync(Order order)
        {
            var charge = ChargeId();
            var result = await Store.RecordPaymentAsync(UserId, order.Id, charge, order.Stars, Token);
            Assert.True(result.Accepted);
            return charge;
        }

        public async Task<Order> ProvisioningOrderAsync()
        {
            var order = await QuoteAsync();
            await PayAsync(order);
            Assert.True(await Store.BeginProvisionAsync(order.Id, Token));
            await Store.SaveProviderOrderAsync(order.Id, "integration-" + Guid.NewGuid().ToString("N"), Token);
            return await OrderAsync(order.Id);
        }

        public ProviderProfile Profile() => new("integration-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), "LPA:1$integration.invalid$" + Guid.NewGuid().ToString("N"), "test-apn", "GOT_RESOURCE");

        public async Task<Order> OrderAsync(string id)
            => await Store.GetAsync(id, UserId, Token) ?? throw new InvalidOperationException("Expected synthetic order is missing.");

        public async Task<Payment> PaymentAsync(string chargeId)
        {
            await using var db = await Database.OpenAsync(Token);
            return await db.QuerySingleAsync<Payment>(new CommandDefinition("""
                SELECT charge_id AS ChargeId,order_id AS OrderId,payer_id AS UserId,stars AS Stars,state AS State,accepted_at AS AcceptedAt
                FROM payments WHERE charge_id=@ChargeId
                """, new { ChargeId = chargeId }, cancellationToken: Token));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_requestKeys.Count == 0 && _chargeIds.Count == 0) return;
                // Use a fresh bounded token: failed/cancelled assertions must not bypass cleanup.
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var ct = cleanupTimeout.Token;
                await using var db = await Database.OpenAsync(ct);
                var ids = (await db.QueryAsync<string>(new CommandDefinition(
                    "SELECT id FROM orders WHERE request_key IN @Keys", new { Keys = _requestKeys.ToArray() }, cancellationToken: ct))).ToArray();
                await using (var transaction = await db.BeginTransactionAsync(ct))
                {
                    await db.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM payments WHERE charge_id IN @Charges OR order_id IN @Ids",
                        new { Charges = _chargeIds.ToArray(), Ids = ids }, transaction, cancellationToken: ct));
                    await db.ExecuteAsync(new CommandDefinition("DELETE FROM esims WHERE order_id IN @Ids", new { Ids = ids }, transaction, cancellationToken: ct));
                    await db.ExecuteAsync(new CommandDefinition("DELETE FROM orders WHERE id IN @Ids", new { Ids = ids }, transaction, cancellationToken: ct));
                    await transaction.CommitAsync(ct);
                }
                var remaining = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
                    SELECT (SELECT COUNT(*) FROM orders WHERE request_key IN @Keys)
                         + (SELECT COUNT(*) FROM payments WHERE charge_id IN @Charges OR order_id IN @Ids)
                         + (SELECT COUNT(*) FROM esims WHERE order_id IN @Ids)
                    """, new { Keys = _requestKeys.ToArray(), Charges = _chargeIds.ToArray(), Ids = ids }, cancellationToken: ct));
                Assert.Equal(0, remaining);
            }
            finally { _timeout.Dispose(); }
        }
    }
}

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ESIMBOT_INTEGRATION_CONFIG")))
            Skip = "Set ESIMBOT_INTEGRATION_CONFIG explicitly to run real database integration checks.";
    }
}

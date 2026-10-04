using System.Security.Cryptography;
using Dapper;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace EsimBot.Tests;

public sealed class SandboxIsolationIntegrationTests
{
    [DatabaseFact]
    public async Task Sandbox_is_bound_and_permission_isolated_and_its_payment_lifecycle_never_issues_an_esim()
    {
        var path = Path.GetFullPath(Environment.GetEnvironmentVariable("ESIMBOT_INTEGRATION_CONFIG")
            ?? throw new InvalidOperationException("Explicit integration configuration is required."));
        using var testConfig = Configuration(path, true);
        using var productionConfig = Configuration(path, false);
        IDatabaseConnectionFactory testDatabase = new DatabaseConnectionFactory(testConfig);
        IDatabaseConnectionFactory productionDatabase = new DatabaseConnectionFactory(productionConfig);
        var protector = new SecretProtector(Options.Create(new StorageOptions
        { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        IPaymentsRepository payments = new PaymentsRepository();
        IOrderService orders = new OrderService(testDatabase, new OrdersRepository(), payments, new EsimsRepository(), protector);
        var requestKey = "sandbox-isolation-" + Guid.NewGuid().ToString("N");
        var chargeId = "sandbox-isolation-" + Guid.NewGuid().ToString("N");
        var userId = Random.Shared.NextInt64(10_000_000_000, 1_000_000_000_000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;

        try
        {
            await using (var test = await testDatabase.OpenAsync(ct))
            {
                Assert.Equal("esim_bot_test", await test.ExecuteScalarAsync<string>("SELECT DATABASE()"));
                Assert.Equal("test", await MarkerAsync(test));
                await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseEnvironment.GuardBindingAsync(test, "production", ct));
                Assert.Equal("test", await MarkerAsync(test));
                var denied = await Assert.ThrowsAsync<MySqlException>(() => test.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM esim_bot.orders"));
                Assert.Contains(denied.Number, new[] { 1044, 1142 });
            }
            await using (var production = await productionDatabase.OpenAsync(ct))
            {
                Assert.Equal("esim_bot", await production.ExecuteScalarAsync<string>("SELECT DATABASE()"));
                Assert.Equal("production", await MarkerAsync(production));
                var denied = await Assert.ThrowsAsync<MySqlException>(() => production.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM esim_bot_test.orders"));
                Assert.Contains(denied.Number, new[] { 1044, 1142 });
            }

            var package = new EsimPackage("__payment_test__", "Sandbox isolation regression", 1, "USD", 1,
                1, "DAY", [new Country("AZ", "Azerbaijan")], "", "", "", "");
            var quote = await orders.CreateQuoteAsync(userId, package, 1, "sandbox-integration", requestKey, ct);
            var accepted = await orders.RecordPaymentAsync(userId, quote.Id, chargeId, 1, ct);
            Assert.True(accepted.Accepted);
            Assert.True(await orders.QueueUnsubmittedRefundAsync(quote.Id, ct));
            await using (var session = await testDatabase.OpenSessionAsync(ct))
            {
                var payment = await payments.GetAsync(session, chargeId, false, ct);
                Assert.NotNull(payment);
                Assert.Equal("refund_pending", payment.State);
                await orders.CompleteRefundAsync(new Payment(payment.ChargeId, payment.OrderId, payment.PayerId,
                    payment.Stars, payment.State, payment.AcceptedAt), ct);
            }
            Assert.Equal("refunded", (await orders.GetAsync(quote.Id, userId, ct))!.State);
            Assert.Null(await orders.GetProfileAsync(quote.Id, userId, ct));
            await using var check = await testDatabase.OpenAsync(ct);
            Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM esims WHERE order_id=@Id", new { quote.Id }));
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var db = await testDatabase.OpenAsync(cleanupTimeout.Token);
            var ids = (await db.QueryAsync<string>(new CommandDefinition("SELECT id FROM orders WHERE request_key=@Key",
                new { Key = requestKey }, cancellationToken: cleanupTimeout.Token))).ToArray();
            await using (var transaction = await db.BeginTransactionAsync(cleanupTimeout.Token))
            {
                await db.ExecuteAsync(new CommandDefinition("DELETE FROM payments WHERE charge_id=@ChargeId OR order_id IN @Ids",
                    new { ChargeId = chargeId, Ids = ids }, transaction, cancellationToken: cleanupTimeout.Token));
                await db.ExecuteAsync(new CommandDefinition("DELETE FROM esims WHERE order_id IN @Ids",
                    new { Ids = ids }, transaction, cancellationToken: cleanupTimeout.Token));
                await db.ExecuteAsync(new CommandDefinition("DELETE FROM orders WHERE id IN @Ids",
                    new { Ids = ids }, transaction, cancellationToken: cleanupTimeout.Token));
                await transaction.CommitAsync(cleanupTimeout.Token);
            }
            var remaining = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT (SELECT COUNT(*) FROM orders WHERE request_key=@Key)
                     + (SELECT COUNT(*) FROM payments WHERE charge_id=@ChargeId OR order_id IN @Ids)
                     + (SELECT COUNT(*) FROM esims WHERE order_id IN @Ids)
                """, new { Key = requestKey, ChargeId = chargeId, Ids = ids }, cancellationToken: cleanupTimeout.Token));
            Assert.Equal(0, remaining);
            Assert.Equal("test", await MarkerAsync(db));
        }
    }

    private static ConfigurationRoot Configuration(string path, bool testing) => (ConfigurationRoot)new ConfigurationBuilder()
        .SetBasePath(Path.GetDirectoryName(path)!).AddJsonFile(Path.GetFileName(path), optional: false)
        .AddInMemoryCollection(new Dictionary<string, string?> { ["PaymentTesting:Enabled"] = testing.ToString() }).Build();

    private static Task<string?> MarkerAsync(MySqlConnection db) => db.ExecuteScalarAsync<string>(
        "SELECT `value` FROM app_state WHERE `key`='payment_environment'");
}

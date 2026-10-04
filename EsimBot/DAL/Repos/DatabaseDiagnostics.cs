using System.Data;
using Dapper;
using MySqlConnector;

namespace EsimBot.DAL.Repos;

/// <summary>Checks the application account and constraints without retaining synthetic business data.</summary>
public static class DatabaseDiagnostics
{
    public static async Task RunAsync(IDatabaseConnectionFactory database, IAppStateRepository state, ISecretProtector protector, CancellationToken ct)
    {
        await state.VerifyAsync(ct);
        await using var db = await database.OpenAsync(ct);
        var metadata = await db.QuerySingleAsync<Metadata>(new CommandDefinition(
            "SELECT @@version AS ServerVersion,DATABASE() AS DatabaseName,CURRENT_USER() AS Account", cancellationToken: ct));
        var tls = await db.QuerySingleAsync<TlsStatus>(new CommandDefinition(
            "SHOW SESSION STATUS LIKE 'Ssl_cipher'", cancellationToken: ct));
        if (string.IsNullOrWhiteSpace(tls.Value)) throw new InvalidOperationException("Database connection has no TLS cipher.");
        if (metadata.DatabaseName != "esim_bot" || !metadata.Account.StartsWith("esim_bot_app@", StringComparison.Ordinal))
            throw new InvalidOperationException("Diagnostics requires the dedicated esim_bot application account and database.");
        Console.WriteLine($"Database: {metadata.DatabaseName}; server version: {metadata.ServerVersion}; application account: {metadata.Account}; TLS: {tls.Value}.");

        var grants = (await db.QueryAsync<string>(new CommandDefinition("SHOW GRANTS FOR CURRENT_USER", cancellationToken: ct))).AsList();
        ValidateApplicationGrants(grants);
        // SHOW GRANTS on MySQL 8 returns privileges and account names, not passwords or authentication hashes.
        foreach (var grant in grants) Console.WriteLine($"Application grant: {grant}");
        var tableCounts = await db.QueryAsync<TableCount>(new CommandDefinition("""
            SELECT 'app_state' AS TableName,COUNT(*) AS RowCount FROM app_state
            UNION ALL SELECT 'orders',COUNT(*) FROM orders
            UNION ALL SELECT 'payments',COUNT(*) FROM payments
            UNION ALL SELECT 'esims',COUNT(*) FROM esims
            """, cancellationToken: ct));
        foreach (var table in tableCounts) Console.WriteLine($"Table {table.TableName}: {table.RowCount} row(s).");

        var marker = Guid.NewGuid().ToString("N");
        var chargeId = "diagnostic-" + marker;
        const string activation = "LPA:1$diagnostic.invalid$rollback-only";
        var ciphertext = protector.Encrypt(marker, activation);
        await using (var transaction = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            try
            {
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO orders (id,user_id,request_key,package_code,package_name,cost_units,stars,terms_version,expires_at)
                    VALUES (@Id,1,@RequestKey,'diagnostic-package','Rollback-only diagnostic',10000,100,'diagnostic',UTC_TIMESTAMP(6)+INTERVAL 15 MINUTE)
                    """, new { Id = marker, RequestKey = "diagnostic-" + marker }, transaction, cancellationToken: ct));
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO payments (charge_id,order_id,payer_id,stars,state,accepted_at)
                    VALUES (@ChargeId,@OrderId,1,100,'accepted',UTC_TIMESTAMP(6))
                    """, new { ChargeId = chargeId, OrderId = marker }, transaction, cancellationToken: ct));

                await ExpectDatabaseErrorAsync(db, transaction, 1062, "A charge identifier can only be recorded once", """
                    INSERT INTO payments (charge_id,order_id,payer_id,stars,state)
                    VALUES (@ChargeId,@OrderId,1,100,'refund_pending')
                    """, new { ChargeId = chargeId, OrderId = marker }, ct);
                await ExpectDatabaseErrorAsync(db, transaction, 1062, "Only one accepted payment is allowed per order", """
                    INSERT INTO payments (charge_id,order_id,payer_id,stars,state,accepted_at)
                    VALUES (@ChargeId,@OrderId,1,100,'accepted',UTC_TIMESTAMP(6))
                    """, new { ChargeId = chargeId + "-second", OrderId = marker }, ct);
                await ExpectDatabaseErrorAsync(db, transaction, 1452, "A payment cannot reference a missing order", """
                    INSERT INTO payments (charge_id,order_id,payer_id,stars,state)
                    VALUES (@ChargeId,@OrderId,1,100,'refund_pending')
                    """, new { ChargeId = chargeId + "-orphan", OrderId = Guid.NewGuid().ToString("N") }, ct);
                await ExpectDatabaseErrorAsync(db, transaction, 3819, "Invalid order states are rejected", """
                    UPDATE orders SET state='invalid' WHERE id=@Id
                    """, new { Id = marker }, ct);
                await ExpectDatabaseErrorAsync(db, transaction, 3819, "Nonpositive prices are rejected", """
                    UPDATE orders SET cost_units=0 WHERE id=@Id
                    """, new { Id = marker }, ct);
                await ExpectDatabaseErrorAsync(db, transaction, 3819, "An accepted payment must reference an order", """
                    INSERT INTO payments (charge_id,order_id,payer_id,stars,state,accepted_at)
                    VALUES (@ChargeId,NULL,1,100,'accepted',UTC_TIMESTAMP(6))
                    """, new { ChargeId = chargeId + "-unbound" }, ct);
                await db.ExecuteAsync(new CommandDefinition("UPDATE payments SET state='refunded' WHERE charge_id=@ChargeId",
                    new { ChargeId = chargeId }, transaction, cancellationToken: ct));
                await ExpectDatabaseErrorAsync(db, transaction, 1062, "The original purchase payment remains unique after its refund", """
                    INSERT INTO payments (charge_id,order_id,payer_id,stars,state,accepted_at)
                    VALUES (@ChargeId,@OrderId,1,100,'accepted',UTC_TIMESTAMP(6))
                    """, new { ChargeId = chargeId + "-historical", OrderId = marker }, ct);

                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO esims (order_id,provider_esim_id,iccid,activation_ciphertext,apn)
                    VALUES (@OrderId,@ProviderId,'diagnostic-iccid',@Ciphertext,NULL)
                    """, new { OrderId = marker, ProviderId = "diagnostic-" + marker, Ciphertext = ciphertext }, transaction, cancellationToken: ct));
                var stored = await db.QuerySingleAsync<string>(new CommandDefinition(
                    "SELECT activation_ciphertext FROM esims WHERE order_id=@OrderId", new { OrderId = marker }, transaction, cancellationToken: ct));
                if (stored == activation || protector.Decrypt(marker, stored) != activation)
                    throw new InvalidOperationException("Encrypted activation storage roundtrip failed.");
                Console.WriteLine("Verified encrypted activation roundtrip using the configured application key.");
            }
            finally
            {
                // Rollback must still run if the caller cancels or an assertion fails.
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        var remaining = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM orders WHERE id=@Id)
                 + (SELECT COUNT(*) FROM payments WHERE charge_id LIKE @ChargePrefix)
                 + (SELECT COUNT(*) FROM esims WHERE order_id=@Id)
            """, new { Id = marker, ChargePrefix = chargeId + "%" }, cancellationToken: ct));
        if (remaining != 0) throw new InvalidOperationException("Rollback verification found retained diagnostic rows.");
        Console.WriteLine("Database diagnostics passed. Transaction rolled back; zero diagnostic rows remain.");
    }

    private static async Task ExpectDatabaseErrorAsync(MySqlConnection db, MySqlTransaction transaction, int errorNumber,
        string assertion, string sql, object parameters, CancellationToken ct)
    {
        try
        {
            await db.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.Number == errorNumber)
        {
            Console.WriteLine($"Verified: {assertion} (MySQL {errorNumber}).");
            return;
        }
        throw new InvalidOperationException($"Database invariant was not enforced: {assertion}.");
    }

    private static void ValidateApplicationGrants(IReadOnlyList<string> grants)
    {
        var allowed = new HashSet<string>(["SELECT", "INSERT", "UPDATE", "DELETE"], StringComparer.Ordinal);
        var granted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            var on = grant.IndexOf(" ON ", StringComparison.Ordinal);
            var to = grant.IndexOf(" TO ", StringComparison.Ordinal);
            if (!grant.StartsWith("GRANT ", StringComparison.Ordinal) || on < 6 || to <= on
                || grant.Contains("WITH GRANT OPTION", StringComparison.Ordinal))
                throw new InvalidOperationException("The application account has unexpected privileges or role grants.");
            var permissions = grant[6..on].Split(", ", StringSplitOptions.RemoveEmptyEntries);
            var scope = grant[(on + 4)..to];
            if (permissions is ["USAGE"] && scope == "*.*") continue;
            if (scope != "`esim_bot`.*" || permissions.Any(permission => !allowed.Contains(permission)))
                throw new InvalidOperationException("The application account has privileges outside the dedicated data tables.");
            granted.UnionWith(permissions);
        }
        if (!granted.SetEquals(allowed))
            throw new InvalidOperationException("The application account is missing required data privileges.");
    }

    private sealed record Metadata(string ServerVersion, string DatabaseName, string Account);
    private sealed record TlsStatus(string Variable_name, string Value);
    private sealed record TableCount(string TableName, long RowCount);
}

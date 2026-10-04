using Dapper;
using EsimBot.DAL.DAO;

namespace EsimBot.DAL.Repos;

public sealed class EsimsRepository : IEsimsRepository
{
    public Task<long> CountAsync(IDatabaseSession session, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM esims", transaction: db.Transaction, cancellationToken: ct));
    }

    public Task<ProfileDao?> GetAsync(IDatabaseSession session, string orderId, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.QuerySingleOrDefaultAsync<ProfileDao>(new CommandDefinition("""
            SELECT order_id AS OrderId,provider_esim_id AS ProviderEsimId,iccid AS Iccid,activation_ciphertext AS ActivationCiphertext,apn AS Apn
            FROM esims WHERE order_id=@OrderId
            """, new { OrderId = orderId }, db.Transaction, cancellationToken: ct));
    }

    public Task InsertAsync(IDatabaseSession session, ProfileDao profile, CancellationToken ct)
    {
        var db = DatabaseSession.Require(session);
        return db.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO esims (order_id,provider_esim_id,iccid,activation_ciphertext,apn)
            VALUES (@OrderId,@ProviderEsimId,@Iccid,@ActivationCiphertext,@Apn)
            """, profile, db.Transaction, cancellationToken: ct));
    }
}

using EsimBot.DAL.DAO;

namespace EsimBot.DAL.Repos;

public interface IEsimsRepository
{
    Task<long> CountAsync(IDatabaseSession session, CancellationToken ct) => throw new NotSupportedException();
    Task<ProfileDao?> GetAsync(IDatabaseSession session, string orderId, CancellationToken ct);
    Task InsertAsync(IDatabaseSession session, ProfileDao profile, CancellationToken ct);
}

using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface ICatalogService
{
    Task<IReadOnlyList<EsimPackage>> GetPackagesAsync(CancellationToken ct);
}

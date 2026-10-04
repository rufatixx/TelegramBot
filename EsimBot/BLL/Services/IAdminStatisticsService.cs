using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface IAdminStatisticsService
{
    Task<AdminStatistics?> GetAsync(long adminUserId, CancellationToken ct);
}

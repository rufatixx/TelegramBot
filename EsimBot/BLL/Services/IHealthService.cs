using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface IHealthService
{
    ApiResponse Live();
    ApiResponse Configuration();
    ApiResponse Ready();
}

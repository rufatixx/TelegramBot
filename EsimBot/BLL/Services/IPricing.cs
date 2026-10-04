using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface IPricing
{
    int StarsFor(EsimPackage package);
    decimal RetailUsdFor(EsimPackage package);
    bool CoversQuote(long costUnits, int stars);
}

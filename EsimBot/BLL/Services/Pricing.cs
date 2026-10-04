using EsimBot.BLL.DTO;
using Microsoft.Extensions.Options;

namespace EsimBot.BLL.Services;

public sealed class Pricing(IOptions<SalesOptions> options, IStarRevenueService? revenue = null) : IPricing
{
    public int StarsFor(EsimPackage package)
    {
        var retailUsd = RetailUsdFor(package);
        var config = options.Value;
        var retention = revenue?.RevenueRetention ?? 1m;
        if (retention is <= 0 or > 1) throw new InvalidOperationException("Telegram revenue estimate is unavailable.");
        return checked((int)Math.Ceiling(retailUsd / (config.NetUsdPerStar * retention)));
    }

    public decimal RetailUsdFor(EsimPackage package)
    {
        if (package.Currency != "USD")
            throw new InvalidOperationException("Package currency is unsupported.");
        var config = options.Value;
        if (package.PriceUnits <= 0 || config.NetUsdPerStar <= 0
            || config.NetUsdPerStar > 1 || config.MarkupPercent < 0 || config.MinimumProfitUsd < 0)
            throw new InvalidOperationException("Pricing is not configured or package currency is unsupported.");
        var cost = package.PriceUsd;
        var profit = Math.Max(cost * config.MarkupPercent / 100m, config.MinimumProfitUsd);
        return cost + profit;
    }

    public bool CoversQuote(long costUnits, int stars) => stars > 0 && stars >= StarsFor(
        new EsimPackage("quote", "quote", costUnits, "USD", 1, 1, "DAY", [], "", "", "", ""));
}

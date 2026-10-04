namespace EsimBot.BLL.Services;

public interface IStarRevenueService
{
    bool IsReady { get; }
    decimal RevenueRetention { get; }
    Task RefreshAsync(CancellationToken ct);
    Task RunAsync(CancellationToken ct);
}

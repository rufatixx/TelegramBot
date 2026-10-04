namespace EsimBot.BLL.Services;

public interface IBalanceMonitorService
{
    /// <summary>Queues an administrator warning without performing network or database work.</summary>
    void ReportInsufficient(long requiredUnits, long balanceUnits);
    Task RunAsync(CancellationToken ct);
}

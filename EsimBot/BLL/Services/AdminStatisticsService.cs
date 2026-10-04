using EsimBot.BLL.DTO;
using EsimBot.DAL.Repos;
using Microsoft.Extensions.Options;

namespace EsimBot.BLL.Services;

public sealed class AdminStatisticsService(
    IDatabaseConnectionFactory database,
    IAppStateRepository appState,
    IOrdersRepository orders,
    IPaymentsRepository payments,
    IEsimsRepository esims,
    IEsimAccessClient provider,
    IBotRuntimeState runtime,
    IOptions<BotOptions> options,
    ILogger<AdminStatisticsService> logger) : IAdminStatisticsService
{
    public async Task<AdminStatistics?> GetAsync(long adminUserId, CancellationToken ct)
    {
        if (adminUserId <= 0 || adminUserId != options.Value.AdminUserId) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var work = timeout.Token;
        var balanceTask = GetBalanceAsync(work);
        try
        {
            await using var session = await database.OpenSessionAsync(work);
            var orderStats = await orders.GetStatisticsAsync(session, work);
            var paymentStats = await payments.GetStatisticsAsync(session, work);
            var issuedEsims = await esims.CountAsync(session, work);
            var users = await appState.CountUsersAsync(work);

            return new AdminStatistics(users,
                new OrderStatistics(orderStats.Buyers, orderStats.TotalOrders, orderStats.OrdersLast24Hours,
                    orderStats.Quoted, orderStats.Paid, orderStats.Provisioning, orderStats.Ready,
                    orderStats.Delivered, orderStats.RefundPending, orderStats.Refunded, orderStats.ManualReview),
                new PaymentStatistics(paymentStats.TotalPayments, paymentStats.SuccessfulPayments,
                    paymentStats.SuccessfulLast24Hours, paymentStats.Accepted, paymentStats.RefundPending,
                    paymentStats.Refunded, paymentStats.GrossStars, paymentStats.RefundedStars), issuedEsims,
                await balanceTask, runtime.IsReady, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Administrator statistics are unavailable ({ExceptionType})", ex.GetType().Name);
            try { await balanceTask; } catch (Exception) { }
            return null;
        }
    }

    private async Task<long?> GetBalanceAsync(CancellationToken ct)
    {
        if (!provider.IsConfigured) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await provider.GetBalanceUnitsAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Provider balance is unavailable for administrator statistics ({ExceptionType})", ex.GetType().Name);
            return null;
        }
    }
}

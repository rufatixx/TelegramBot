namespace EsimBot.BLL.Services;

public interface IFulfillment
{
    Task RunAsync(CancellationToken ct);
}

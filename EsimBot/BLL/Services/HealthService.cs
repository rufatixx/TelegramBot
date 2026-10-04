using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public sealed class HealthService(IBotRuntimeState runtime, StartupConfiguration configuration) : IHealthService
{
    public ApiResponse Live() => new(StatusCodes.Status200OK, new { status = "alive" });
    public ApiResponse Configuration() => new(StatusCodes.Status200OK, configuration);
    // Pausing new sales must not remove the receiver from routing while paid orders still need reconciliation.
    public ApiResponse Ready() => runtime.IsReady && configuration.CanStartWorker
        ? new(StatusCodes.Status200OK, new { status = "ready" })
        : new(StatusCodes.Status503ServiceUnavailable, new { status = "not_ready", botRunning = runtime.IsReady, configuration });
}

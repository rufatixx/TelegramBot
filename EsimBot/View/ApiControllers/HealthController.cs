using EsimBot.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsimBot.View.ApiControllers;

[ApiController]
[Route("health")]
public sealed class HealthController(IHealthService service) : ControllerBase
{
    [HttpGet("live")]
    public IActionResult Live()
    {
        var result = service.Live();
        return StatusCode(result.StatusCode, result.Payload);
    }

    [HttpGet("config")]
    public IActionResult Configuration()
    {
        var result = service.Configuration();
        return StatusCode(result.StatusCode, result.Payload);
    }

    [HttpGet("ready")]
    public IActionResult Ready()
    {
        var result = service.Ready();
        return StatusCode(result.StatusCode, result.Payload);
    }
}

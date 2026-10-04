using EsimBot.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsimBot.View.ApiControllers;

[ApiController]
[Route("api/telegram")]
public sealed class TelegramWebhookController(ITelegramWebhookService service) : ControllerBase
{
    [HttpPost("webhook")]
    [RequestSizeLimit(WebhookSecurity.MaxWebhookBytes)]
    public async Task<IActionResult> ReceiveAsync()
    {
        var result = await service.ReceiveAsync(HttpContext, HttpContext.RequestAborted);
        return StatusCode(result.StatusCode, result.Payload);
    }
}

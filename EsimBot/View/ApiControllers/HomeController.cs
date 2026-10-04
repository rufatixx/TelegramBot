using EsimBot.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace EsimBot.View.ApiControllers;

public sealed class HomeController(IHomeService service) : Controller
{
    [HttpGet("/")]
    [HttpGet("/home/index")]
    [HttpGet("/{language:regex(^(ru|az|en)$)}")]
    [OutputCache(Duration = 300)]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public IActionResult Index([FromRoute] string? language = null)
    {
        var result = service.Index(language);
        return View("~/View/Pages/Home/Index.cshtml", result);
    }

    [HttpGet("/robots.txt")]
    [OutputCache(Duration = 3600)]
    public IActionResult Robots()
    {
        var result = service.Robots();
        return Content(result, "text/plain; charset=utf-8");
    }

    [HttpGet("/sitemap.xml")]
    [OutputCache(Duration = 3600)]
    public IActionResult Sitemap()
    {
        var result = service.Sitemap();
        return Content(result, "application/xml; charset=utf-8");
    }
}

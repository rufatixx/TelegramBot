using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using EsimBot.View.ApiControllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EsimBot.Tests;

public sealed class LandingPageTests
{
    [Theory]
    [InlineData(null, "ru", "https://esimbot.digify.az/")]
    [InlineData("ru", "ru", "https://esimbot.digify.az/")]
    [InlineData("az", "az", "https://esimbot.digify.az/az")]
    [InlineData("AZ", "az", "https://esimbot.digify.az/az")]
    [InlineData("en", "en", "https://esimbot.digify.az/en")]
    [InlineData("<script>", "ru", "https://esimbot.digify.az/")]
    public void Canonicals_are_fixed_and_each_translation_has_complete_content(string? requested, string language, string canonical)
    {
        var service = new HomeService();
        var page = service.Index(requested);
        Assert.Equal(language, page.Language);
        Assert.Equal(canonical, page.CanonicalUrl);
        Assert.Equal(service.Index("ru").Copy.Keys.Order(), page.Copy.Keys.Order());
        Assert.All(page.Copy.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        using var schema = JsonDocument.Parse(page.SchemaJson);
        var nodes = schema.RootElement.GetProperty("@graph").EnumerateArray().ToArray();
        Assert.Equal(3, nodes.Length);
        var webPage = nodes.Single(node => node.GetProperty("@type").GetString() == "WebPage");
        Assert.Equal(canonical, webPage.GetProperty("url").GetString());
        Assert.Equal(page.Title, webPage.GetProperty("name").GetString());
        Assert.Equal(language, webPage.GetProperty("inLanguage").GetString());
        Assert.DoesNotContain("aggregateRating", page.SchemaJson);
        Assert.DoesNotContain("</script", page.SchemaJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sitemap_lists_only_canonical_languages_with_reciprocal_alternates()
    {
        var service = new HomeService();
        var document = XDocument.Parse(service.Sitemap());
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var urls = document.Root!.Elements(ns + "url").ToArray();
        Assert.Equal(3, urls.Length);
        Assert.Equal(new[] { "https://esimbot.digify.az/", "https://esimbot.digify.az/az", "https://esimbot.digify.az/en" },
            urls.Select(node => node.Element(ns + "loc")!.Value).Order());
        Assert.All(urls, url => Assert.Equal(new[] { "az", "en", "ru", "x-default" },
            url.Elements(xhtml + "link").Select(link => (string)link.Attribute("hreflang")!).Order()));
        Assert.Contains("Sitemap: https://esimbot.digify.az/sitemap.xml", service.Robots());
        Assert.Contains("Disallow: /api/", service.Robots());
    }

    [Fact]
    public async Task Real_compiled_Razor_renders_all_languages_without_a_bot_host_or_database()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../EsimBot"));
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(HomeService).Assembly.GetName().Name,
            ContentRootPath = root,
            EnvironmentName = "Production"
        });
        // EmptyBuilder deliberately loads no appsettings, secrets or environment configuration.
        // This is not Program: no hosted workers, Telegram or DB services.
        builder.WebHost.UseKestrel(); // Register IServer for MVC DI only; never bind or start it.
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(HomeController).Assembly);
        await using var app = builder.Build(); // Never Start/Run this app.
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var context = new ActionContext(new DefaultHttpContext { RequestServices = services }, new RouteData(), new ActionDescriptor());
        var view = services.GetRequiredService<IRazorViewEngine>().GetView(null, "/View/Pages/Home/Index.cshtml", true);
        Assert.True(view.Success, string.Join(", ", view.SearchedLocations ?? []));
        foreach (var language in new[] { "ru", "az", "en" })
        {
            var page = new HomeService().Index(language);
            using var writer = new StringWriter();
            var viewData = new ViewDataDictionary<LandingPageDto>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = page };
            var viewContext = new ViewContext(context, view.View, viewData,
                new TempDataDictionary(context.HttpContext, services.GetRequiredService<ITempDataProvider>()), writer, new HtmlHelperOptions());
            await view.View.RenderAsync(viewContext);
            var html = writer.ToString();
            Assert.Contains($"lang=\"{language}\"", html);
            Assert.Contains(page.CanonicalUrl, html);
            Assert.Contains("application/ld+json", html);
            Assert.Contains("https://t.me/RoamiSIMBot", html);
            Assert.Contains("https://t.me/RoamiSIMBot?start=landing", html);
            Assert.Contains("https://t.me/RoamiSIMBot?start=support", html);
            Assert.Contains("https://t.me/RoamiSIMBot?start=terms", html);
            Assert.Contains("/img/flags/tr.svg", html);
            Assert.Contains("/img/flags/az.svg", html);
            Assert.Contains("/img/flags/ge.svg", html);
            Assert.DoesNotContain("flag-tr", html);
            Assert.DoesNotContain("flag-ge", html);
            Assert.Single(Regex.Matches(html, "<h1(?: |>)").Cast<Match>());
            Assert.Equal(4, Regex.Matches(html[..html.IndexOf("</head>", StringComparison.Ordinal)], "hreflang=").Count);
            Assert.Single(Regex.Matches(html, "<script src=").Cast<Match>());
            Assert.Contains("/js/telegram-handoff.js?v=1", html);
            Assert.DoesNotContain("undefined", html);
            foreach (Match href in Regex.Matches(html, "href=\"#([^\"]+)\""))
                Assert.Contains("id=\"" + href.Groups[1].Value + "\"", html);
            // Optional static preview exports already-rendered pages only; it cannot run the bot.
            if (Environment.GetEnvironmentVariable("ESIMBOT_LANDING_PREVIEW") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                var preview = html
                    .Replace("/css/landing.css?v=2", new Uri(Path.Combine(root, "wwwroot/css/landing.css")).AbsoluteUri)
                    .Replace("/css/official-flags.css?v=1", new Uri(Path.Combine(root, "wwwroot/css/official-flags.css")).AbsoluteUri)
                    .Replace("/js/telegram-handoff.js?v=1", new Uri(Path.Combine(root, "wwwroot/js/telegram-handoff.js")).AbsoluteUri);
                foreach (var countryCode in new[] { "tr", "az", "ge" })
                    preview = preview.Replace($"/img/flags/{countryCode}.svg",
                        new Uri(Path.Combine(root, $"wwwroot/img/flags/{countryCode}.svg")).AbsoluteUri);
                await File.WriteAllTextAsync(Path.Combine(output, language + ".html"), preview);
            }
        }
    }

    [Fact]
    public void Mobile_handoff_targets_the_bot_chat_and_keeps_an_https_fallback()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../EsimBot"));
        var script = File.ReadAllText(Path.Combine(root, "wwwroot/js/telegram-handoff.js"));
        Assert.Contains("tg://resolve?domain=${telegramBot}&start=", script);
        Assert.Contains("window.location.assign(webUrl.href)", script);
        Assert.Contains("visibilitychange", script);
        Assert.Contains("^[A-Za-z0-9_-]{1,64}$", script);
    }
}

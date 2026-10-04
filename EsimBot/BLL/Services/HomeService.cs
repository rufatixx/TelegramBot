using System.Collections.Frozen;
using System.Text.Json;
using System.Xml.Linq;
using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

/// <summary>Public, immutable landing data: no customer, supplier or database calls.</summary>
public sealed class HomeService : IHomeService
{
    private const string Origin = "https://esimbot.digify.az";
    private const string Bot = "https://t.me/RoamiSIMBot";
    private static readonly FrozenDictionary<string, LandingPageDto> Pages = new[] { "ru", "az", "en" }
        .ToFrozenDictionary(language => language, CreatePage, StringComparer.Ordinal);

    public LandingPageDto Index(string? language) => Pages.GetValueOrDefault(language?.ToLowerInvariant() ?? "ru", Pages["ru"]);

    public string Robots() => $"User-agent: *\nAllow: /\nDisallow: /api/\nDisallow: /health/\nSitemap: {Origin}/sitemap.xml\n";

    public string Sitemap()
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var pages = Pages.Values.OrderBy(page => page.Language).ToArray();
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "urlset", new XAttribute(XNamespace.Xmlns + "xhtml", xhtml),
                pages.Select(page => new XElement(ns + "url", new XElement(ns + "loc", page.CanonicalUrl),
                    pages.Select(alternate => new XElement(xhtml + "link", new XAttribute("rel", "alternate"),
                        new XAttribute("hreflang", alternate.Language), new XAttribute("href", alternate.CanonicalUrl))),
                    new XElement(xhtml + "link", new XAttribute("rel", "alternate"), new XAttribute("hreflang", "x-default"),
                        new XAttribute("href", Origin + "/")))))).ToString();
    }

    private static LandingPageDto CreatePage(string language)
    {
        using var stream = typeof(HomeService).Assembly.GetManifestResourceStream($"EsimBot.BLL.Services.Landing.{language}.json")
            ?? throw new InvalidOperationException("Missing landing translation.");
        var copy = (JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Empty landing translation.")).ToFrozenDictionary(StringComparer.Ordinal);
        var url = Origin + (language == "ru" ? "/" : "/" + language);
        var locale = language switch { "ru" => "ru_RU", "az" => "az_AZ", _ => "en_US" };
        // Default JSON escaping keeps script terminators and HTML out of the raw JSON-LD block.
        var schema = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@graph"] = new object[]
            {
                new Dictionary<string, object> { ["@type"] = "Organization", ["@id"] = Origin + "/#organization",
                    ["name"] = "RoamiSIM", ["url"] = Origin + "/", ["sameAs"] = new[] { Bot } },
                new Dictionary<string, object> { ["@type"] = "WebSite", ["@id"] = Origin + "/#website",
                    ["name"] = "RoamiSIM", ["url"] = Origin + "/", ["inLanguage"] = new[] { "ru", "az", "en" },
                    ["publisher"] = new Dictionary<string, string> { ["@id"] = Origin + "/#organization" } },
                new Dictionary<string, object> { ["@type"] = "WebPage", ["@id"] = url + "#page", ["url"] = url,
                    ["name"] = copy["meta.title"], ["description"] = copy["meta.description"], ["inLanguage"] = language,
                    ["isPartOf"] = new Dictionary<string, string> { ["@id"] = Origin + "/#website" } }
            }
        });
        return new LandingPageDto(language, locale, copy["meta.title"], copy["meta.description"], url,
            Bot + "?start=landing", copy, schema);
    }
}

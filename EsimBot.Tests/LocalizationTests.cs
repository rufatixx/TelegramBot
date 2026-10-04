using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;

namespace EsimBot.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("Ui")]
    [InlineData("Notifications")]
    [InlineData("Testing")]
    [InlineData("Support")]
    [InlineData("Balance")]
    [InlineData("Countries")]
    public void Every_language_has_matching_nonempty_keys_and_format_arguments(string family)
    {
        var baseline = Resource(family, "en");
        Assert.NotEmpty(baseline);
        foreach (var code in BotText.SupportedCodes)
        {
            var entries = Resource(family, code);
            Assert.Equal(baseline.Keys.Order(), entries.Keys.Order());
            foreach (var (key, value) in entries)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"Empty {code}/{key}");
                Assert.Equal(CompositeFormat.Parse(baseline[key]).MinimumArgumentCount,
                    CompositeFormat.Parse(value).MinimumArgumentCount);
                Assert.Equal(Placeholders(baseline[key]), Placeholders(value));
            }
        }
    }

    [Theory]
    [InlineData("az")]
    [InlineData("ru")]
    [InlineData("en")]
    public void All_message_templates_format_within_Telegram_limits(string code)
    {
        var text = new BotText(code);
        var entries = Resource("Ui", code).Concat(Resource("Notifications", code)).Concat(Resource("Testing", code))
            .Concat(Resource("Support", code)).Concat(Resource("Balance", code)).ToDictionary();
        foreach (var (key, template) in entries)
        {
            var count = CompositeFormat.Parse(template).MinimumArgumentCount;
            var args = Enumerable.Range(0, count).Select(index => (object)$"sample-{index}").ToArray();
            var result = text.Text(key, args);
            Assert.NotEqual(key, result);
            Assert.InRange(VisibleLength(result), 1, 4096);
            if (key.StartsWith("ui.command_", StringComparison.Ordinal)) Assert.InRange(result.Length, 1, 256);
        }

        var id = new string('a', 32);
        Assert.InRange(VisibleLength(text.Text("notifications.ready_caption", new string('P', 160), id)), 1, 1024);
        Assert.InRange(text.Text("ui.invoice_description", id).Length, 1, 255);
        var manual = text.Text("notifications.manual_code", new string('A', 2000));
        var apn = text.Text("notifications.apn", new string('p', 120));
        Assert.InRange(VisibleLength(text.Text("notifications.install", manual, apn)), 1, 4096);
        foreach (var key in entries.Keys.Where(key => key.StartsWith("ui.checkout_", StringComparison.Ordinal)))
            Assert.InRange(text.Text(key).Length, 1, 200);
    }

    [Fact]
    public void Literal_and_command_message_keys_referenced_by_business_code_exist_in_all_languages()
    {
        var project = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "EsimBot"));
        var references = Directory.EnumerateFiles(Path.Combine(project, "BLL"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "\"((?:ui|notifications|testing|support|balance)\\.[a-z0-9_]+)\"")
                .Select(match => match.Groups[1].Value))
            .Where(key => !key.EndsWith('_'))
            .Concat(new[] { "start", "orders", "install", "support", "paysupport", "terms", "language" }
                .Select(command => "ui.command_" + command))
            .Concat(new[] { "start", "testpayment", "orders", "refund", "id", "language" }
                .Select(command => "testing.command_" + command)).Distinct().ToArray();
        Assert.NotEmpty(references);
        foreach (var code in BotText.SupportedCodes)
        {
            var entries = Resource("Ui", code).Concat(Resource("Notifications", code)).Concat(Resource("Testing", code))
                .Concat(Resource("Support", code)).Concat(Resource("Balance", code)).ToDictionary();
            Assert.All(references, key => Assert.True(entries.ContainsKey(key), $"Missing {code}/{key}"));
        }
    }

    [Fact]
    public async Task Concurrent_customers_keep_independent_languages_and_do_not_change_ambient_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var expected = new Dictionary<string, (string Button, string Country)>
        {
            ["az"] = ("🌐 Dil", "Türkiyə"),
            ["ru"] = ("🌐 Язык", "Турция"),
            ["en"] = ("🌐 Language", "Türkiye")
        };
        await Task.WhenAll(Enumerable.Range(0, 90).Select(async index =>
        {
            var code = BotText.SupportedCodes[index % 3];
            var text = new BotText(code);
            await Task.Yield();
            Assert.Equal(code, text.Code);
            Assert.Equal(expected[code].Button, text.Text("ui.button_language"));
            Assert.Equal(expected[code].Country, text.Country(new Country("tr", "Provider country")));
            Assert.Equal(culture, CultureInfo.CurrentCulture);
            Assert.Equal(uiCulture, CultureInfo.CurrentUICulture);
        }));
    }

    [Theory]
    [InlineData("az")]
    [InlineData("ru")]
    [InlineData("en")]
    public void Country_search_accepts_all_three_languages_and_preserves_unknown_provider_names(string code)
    {
        var text = new BotText(code);
        var names = text.CountrySearchNames(new Country("TR", "Turkey")).ToArray();
        Assert.Contains("Türkiyə", names);
        Assert.Contains("Турция", names);
        Assert.Contains("Türkiye", names);
        Assert.Contains("Turkey", names);
        Assert.Contains("TR", names);
        Assert.Equal("Provider-only territory", text.Country(new Country("NOT_A_CODE", "Provider-only territory")));
        foreach (var policy in new[] { "install", "first_connection", "unknown-policy" })
            Assert.DoesNotContain("ui.", text.Activation(policy));
    }

    [Fact]
    public void Unknown_language_falls_back_to_English()
    {
        var text = new BotText("unsupported");
        Assert.Equal("en", text.Code);
        Assert.Equal("🌐 Language", text.Text("ui.button_language"));
    }

    private static Dictionary<string, string> Resource(string family, string code)
    {
        var assembly = typeof(BotText).Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), name =>
            name.EndsWith($".Localization.{family}.{code}.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(properties.Length, properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count());
        return properties.ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

    private static int[] Placeholders(string template) => Regex.Matches(template, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();

    private static int VisibleLength(string text) => WebUtility.HtmlDecode(Regex.Replace(text, "</?(?:b|code)>", "")).Length;
    private static string SourceFile([CallerFilePath] string file = "") => file;
}

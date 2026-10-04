using System.Globalization;
using System.Reflection;
using System.Text.Json;
namespace EsimBot.BLL.DTO;

/// <summary>Immutable per-request language context. No shared mutable culture or language state.</summary>
public sealed class BotText
{
    public static IReadOnlyList<string> SupportedCodes { get; } = Array.AsReadOnly(new[] { "az", "ru", "en" });
    private static readonly Lazy<IReadOnlyDictionary<string, Dictionary<string, string>>> Messages = new(() =>
        SupportedCodes.ToDictionary(code => code, code => ReadResources(code, countries: false)));
    private static readonly Lazy<IReadOnlyDictionary<string, Dictionary<string, string>>> Countries = new(() =>
        SupportedCodes.ToDictionary(code => code, code => ReadResources(code, countries: true)));

    public BotText(string code)
    {
        Code = SupportedCodes.Contains(code) ? code : "en";
        Culture = CultureInfo.GetCultureInfo(Code switch { "az" => "az-Latn-AZ", "ru" => "ru-RU", _ => "en-US" });
    }

    public string Code { get; }
    public CultureInfo Culture { get; }
    public static BotText For(string code) => new(code);

    public string Text(string key, params object[] arguments)
    {
        var template = Messages.Value[Code].GetValueOrDefault(key, key);
        return arguments.Length == 0 ? template : string.Format(Culture, template, arguments);
    }

    public string Country(Country country) => Countries.Value[Code].GetValueOrDefault(country.Code.ToUpperInvariant(), country.Name);

    public IEnumerable<string> CountrySearchNames(Country country) => SupportedCodes
        .Select(code => Countries.Value[code].GetValueOrDefault(country.Code.ToUpperInvariant(), country.Name))
        .Append(country.Code).Append(country.Name).Distinct(StringComparer.OrdinalIgnoreCase);

    public string Activation(string policy) => Text(policy switch
    {
        "install" => "ui.activation_install",
        "first_connection" => "ui.activation_first_connection",
        _ => "ui.activation_unknown"
    });

    private static Dictionary<string, string> ReadResources(string code, bool countries)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames().Where(name =>
            name.Contains(".Localization.", StringComparison.Ordinal) && name.EndsWith($".{code}.json", StringComparison.Ordinal)
            && name.Contains(".Countries.", StringComparison.Ordinal) == countries).OrderBy(name => name);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException("Language resource is empty.");
            foreach (var entry in entries)
                if (!result.TryAdd(entry.Key, entry.Value)) throw new InvalidOperationException("Duplicate language key: " + entry.Key);
        }
        if (result.Count == 0) throw new InvalidOperationException("Missing language resources: " + code);
        return result;
    }
}

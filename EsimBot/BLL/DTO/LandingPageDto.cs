namespace EsimBot.BLL.DTO;

public sealed record LandingPageDto(string Language, string Locale, string Title, string Description,
    string CanonicalUrl, string BotUrl, IReadOnlyDictionary<string, string> Copy, string SchemaJson)
{
    public string T(string key) => Copy[key];
    public string BotStartUrl(string parameter) => BotUrl.Split('?', 2)[0] + "?start=" + parameter;
}

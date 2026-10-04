using EsimBot.BLL.DTO;

namespace EsimBot.BLL.Services;

public interface IHomeService
{
    LandingPageDto Index(string? language);
    string Robots();
    string Sitemap();
}

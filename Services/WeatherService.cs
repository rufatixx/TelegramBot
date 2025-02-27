using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
namespace TelegramBot.Services
{


    public class WeatherService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public WeatherService(IConfiguration configuration)
        {
            _httpClient = new HttpClient();
           
            _apiKey = configuration["OpenWeather:78a5aca0299351bc4883bc0a946828cb"];
        }

        public async Task<string> GetWeatherInfoAsync(string city)
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={city}&appid={_apiKey}&units=metric";
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                dynamic weatherData = JsonConvert.DeserializeObject(json);
                string description = weatherData.weather[0].description;
                double temp = weatherData.main.temp;
                return $"Weather in {city}: {description}, {temp}°C";
            }
            else
            {
                return $"Could not retrieve weather information for {city}.";
            }
        }
    }

}


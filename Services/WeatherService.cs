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

            var url1 = $"https://api.openweathermap.org/data/2.5/weather?q={city}&appid={_apiKey}&units=metric";
            var response1 = await _httpClient.GetAsync(url1);
            if (!response1.IsSuccessStatusCode)
            {
                var errorContent = await response1.Content.ReadAsStringAsync();
                System.Console.WriteLine($"Error fetching city coordinates: {errorContent}");
                return $"Could not retrieve weather information for {city}.";
            }
            var json1 = await response1.Content.ReadAsStringAsync();
            dynamic data1 = JsonConvert.DeserializeObject(json1);
            double lat = data1.coord.lat;
            double lon = data1.coord.lon;


            var url2 = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&exclude=minutely,alerts&appid={_apiKey}&units=metric";
            var response2 = await _httpClient.GetAsync(url2);
            if (!response2.IsSuccessStatusCode)
            {
                var errorContent = await response2.Content.ReadAsStringAsync();
                System.Console.WriteLine($"Error fetching detailed weather: {errorContent}");
                return $"Could not retrieve extended weather information for {city}.";
            }
            var json2 = await response2.Content.ReadAsStringAsync();
            dynamic data2 = JsonConvert.DeserializeObject(json2);

            string description = data2.weather[0].description;
            double temp = data2.main.temp;
            return $"Current weather in {city}: {description}, {temp}°C.";
        }
    }

}


using Microsoft.AspNetCore.Mvc;
using Telegram.Bot;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherController : ControllerBase
{

    private readonly IUserRepository _userRepository;
    private readonly WeatherService _weatherService;
    private readonly TelegramBotClient _botClient;

    public WeatherController(IUserRepository userRepository, WeatherService weatherService, TelegramBotClient botClient)
    {
        _userRepository = userRepository;
        _weatherService = weatherService;
        _botClient = botClient;
    }

   
    [HttpPost("sendWeatherToAll")]
    public async Task<IActionResult> SendWeatherToAll()
    {
        // Get all users from the database
        IEnumerable<User> users = await _userRepository.GetAllUsersAsync();

        foreach (var user in users)
        {
            // Get real weather info based on the user's default city
            var weatherInfo = await _weatherService.GetWeatherInfoAsync(user.DefaultCity);

            // Send the weather notification to the user's Telegram chat
            await _botClient.SendTextMessageAsync(chatId: user.ChatId, text: weatherInfo);
        }

        return Ok("Weather notifications sent to all users.");
    }
    
}


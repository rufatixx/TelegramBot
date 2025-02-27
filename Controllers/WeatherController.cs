using Microsoft.AspNetCore.Mvc;
using TelegramBot.Services;

namespace TelegramBot.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherController : ControllerBase
{


    private readonly IUserRepository _userRepository;
    private readonly TelegramBotService _telegramBotService;

    public WeatherController(IUserRepository userRepository,TelegramBotService telegramBotService)
    {
        _userRepository = userRepository;
        _telegramBotService = telegramBotService;
    }

   
    [HttpPost("sendWeatherToAll")]
    public async Task<IActionResult> SendWeatherToAll([FromBody] WeatherRequest request)
    {
        
        IEnumerable<User> users;
        if (!string.IsNullOrEmpty(request.UserId))
        {
            var user = await _userRepository.GetUserAsync(request.UserId);
            if (user == null)
            {
                return NotFound($"User with id {request.UserId} not found.");
            }
            users = new List<User> { user };
        }
        else
        {
            users = await _userRepository.GetAllUsersAsync();
        }

        
        string weatherUpdate = "Today's weather is sunny with a slight chance of rain in the afternoon.";

    
        foreach (var user in users)
        {
            await _telegramBotService.SendMessageAsync(user.ChatId, weatherUpdate);
        }

        return Ok("Weather information sent to users.");
    }
    public class WeatherRequest
    {
        public string? UserId { get; set; }  
    }
}


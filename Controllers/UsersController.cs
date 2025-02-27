using Microsoft.AspNetCore.Mvc;

namespace TelegramBot.Controllers;

[ApiController]
[Route("[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _userRepository;

    public UsersController(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    [HttpGet("{userId}")]
    public IActionResult GetUser(string userId)
    {
        
        var user = new
        {
            UserId = userId,
            Name = "John Doe",
            WeatherHistory = new[] { "London: Sunny", "New York: Cloudy" }
        };
        return Ok(user);
    }
}


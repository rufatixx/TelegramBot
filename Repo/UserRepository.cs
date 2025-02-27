using System.Data;
using Dapper;
using Microsoft.Extensions.Configuration;

using System.Threading.Tasks;
using MySqlConnector;

public interface IUserRepository
{
    Task<User> GetUserAsync(string userId);
    Task InsertUserAsync(User user);
    Task SaveWeatherRequestAsync(string userId, string city, string weatherInfo);
}

public class UserRepository : IUserRepository
{
    private readonly string _connectionString;

    public UserRepository(IConfiguration configuration)
    {
        
        _connectionString = configuration.GetConnectionString("DefaultConnection");
    }

    public async Task<User> GetUserAsync(string userId)
    {
        using (IDbConnection db = new MySqlConnection(_connectionString))
        {
            // Retrieve user information from the Users table
            return await db.QueryFirstOrDefaultAsync<User>(
                "SELECT * FROM Users WHERE UserId = @UserId",
                new { UserId = userId });
        }
    }

    public async Task InsertUserAsync(User user)
    {
        using (var connection = new MySqlConnection(_connectionString))
        {
            var sql = "INSERT INTO Users (UserId, Name) VALUES (@UserId, @Name)";
            await connection.ExecuteAsync(sql, user);
        }
    }


    public async Task SaveWeatherRequestAsync(string userId, string city, string weatherInfo)
    {
        using (IDbConnection db = new MySqlConnection(_connectionString))
        {
           
            var sql = @"INSERT INTO WeatherHistory (UserId, City, WeatherInfo, RequestDate) 
                         VALUES (@UserId, @City, @WeatherInfo, NOW())";
            await db.ExecuteAsync(sql, new { UserId = userId, City = city, WeatherInfo = weatherInfo });
        }
    }
}

public class User
{
    public string UserId { get; set; }
    public string Name { get; set; }

}

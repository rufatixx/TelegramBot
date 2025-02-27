using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient; 
using Microsoft.Extensions.Configuration;


public interface IUserRepository
{
    Task<User> GetUserAsync(string userId);
    Task<IEnumerable<User>> GetAllUsersAsync();
    Task InsertUserAsync(User user);
    Task UpdateUserAsync(User user);
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
        using (IDbConnection db = new SqlConnection(_connectionString))
        {
            return await db.QueryFirstOrDefaultAsync<User>(
                "SELECT * FROM Users WHERE UserId = @UserId", new { UserId = userId });
        }
    }

    public async Task<IEnumerable<User>> GetAllUsersAsync()
    {
        using (IDbConnection db = new SqlConnection(_connectionString))
        {
            return await db.QueryAsync<User>("SELECT * FROM Users");
        }
    }

    public async Task InsertUserAsync(User user)
    {
        using (IDbConnection db = new SqlConnection(_connectionString))
        {
            var sql = "INSERT INTO Users (UserId, ChatId, Name, DefaultCity) VALUES (@UserId, @ChatId, @Name, @DefaultCity)";
            await db.ExecuteAsync(sql, user);
        }
    }

    public async Task UpdateUserAsync(User user)
    {
        using (IDbConnection db = new SqlConnection(_connectionString))
        {
            var sql = "UPDATE Users SET ChatId = @ChatId, Name = @Name, DefaultCity = @DefaultCity WHERE UserId = @UserId";
            await db.ExecuteAsync(sql, user);
        }
    }

    public async Task SaveWeatherRequestAsync(string userId, string city, string weatherInfo)
    {
        using (IDbConnection db = new SqlConnection(_connectionString))
        {
            var sql = @"INSERT INTO WeatherHistory (UserId, City, WeatherInfo, RequestDate) 
                        VALUES (@UserId, @City, @WeatherInfo, GETDATE())";
            await db.ExecuteAsync(sql, new { UserId = userId, City = city, WeatherInfo = weatherInfo });
        }
    }
}


public class User
{
    public string UserId { get; set; }
    public long ChatId { get; set; }
    public string Name { get; set; }
    public string DefaultCity { get; set; }
}

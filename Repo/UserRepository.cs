using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

public interface IUserRepository
{
    Task<IEnumerable<User>> GetAllUsersAsync();
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

    public async Task<IEnumerable<User>> GetAllUsersAsync()
    {
        using (var connection = new SqlConnection(_connectionString))
        {
            var sql = "SELECT * FROM Users";
            return await connection.QueryAsync<User>(sql);
        }
    }

    public async Task<User> GetUserAsync(string userId)
    {
        using (IDbConnection db = new SqlConnection(_connectionString))
        {
            
            var sql = "SELECT * FROM Users WHERE UserId = @UserId";
            return await db.QueryFirstOrDefaultAsync<User>(sql, new { UserId = userId });
        }
    }

    public async Task InsertUserAsync(User user)
    {
        using (var connection = new SqlConnection(_connectionString))
        {
           
            var sql = "INSERT INTO Users (UserId, Username, ChatId) VALUES (@UserId, @Name, @ChatId)";
            await connection.ExecuteAsync(sql, user);
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
    public string Name { get; set; }
    public long ChatId { get; set; }
}

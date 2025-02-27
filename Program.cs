using Telegram.Bot;
using TelegramBot.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register MSSQL repository.
builder.Services.AddScoped<IUserRepository, UserRepository>();

// Register TelegramBotClient and WeatherService as singletons.
builder.Services.AddSingleton<TelegramBotClient>(sp => new TelegramBotClient("8095937008:AAEAAzHH49qfJg8cU0JTMDLRr6vCvXMzipA"));
builder.Services.AddSingleton<WeatherService>();

builder.Services.AddSingleton<TelegramBotService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Start the Telegram Bot service.
var botService = app.Services.GetRequiredService<TelegramBotService>();
botService.Initialize();

app.Run();

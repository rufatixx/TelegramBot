using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.DependencyInjection;

namespace TelegramBot.Services
{
    public class TelegramBotService
    {
        private readonly TelegramBotClient _botClient;
        private readonly IServiceScopeFactory _scopeFactory;

        public TelegramBotService(IServiceScopeFactory scopeFactory)
        {
            _botClient = new TelegramBotClient("8095937008:AAEAAzHH49qfJg8cU0JTMDLRr6vCvXMzipA");
            _scopeFactory = scopeFactory;
        }

        public void Initialize()
        {
            _botClient.StartReceiving(
                updateHandler: Bot_OnMessage,
                errorHandler: HandleErrorAsync,
                cancellationToken: CancellationToken.None
            );
            Console.WriteLine("Bot started...");
        }

        private async Task Bot_OnMessage(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Type == UpdateType.Message && update.Message?.Text != null)
            {
                if (update.Message.Text.StartsWith("/weather", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = update.Message.Text.Split(' ');
                    var city = parts.Length > 1 ? parts[1] : "defaultCity";
                    var weatherInfo = $"Dummy weather info for {city}";

                    
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                        var user = await userRepository.GetUserAsync(update.Message.From.Id.ToString());
                        if (user == null)
                        {
                          
                            await userRepository.InsertUserAsync(new User { UserId = update.Message.From.Id.ToString(), Name = $"{update.Message.From.Username}",ChatId =  update.Message.Chat.Id });
                        }
                       
                        await userRepository.SaveWeatherRequestAsync(update.Message.From.Id.ToString(), city, weatherInfo);
                      
                       
                    }

                    await botClient.SendTextMessageAsync(
                        chatId: update.Message.Chat.Id,
                        text: weatherInfo,
                        cancellationToken: cancellationToken
                    );
                }
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Error: {exception.Message}");
            return Task.CompletedTask;
        }
        // Helper method to send a message to a specific chat
        public async Task SendMessageAsync(long chatId, string message)
        {
            await _botClient.SendTextMessageAsync(chatId, message);
        }
    }
}


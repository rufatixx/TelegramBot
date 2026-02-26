using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace TelegramBot.Services
{
    public class TelegramBotService
    {
        private readonly TelegramBotClient _botClient;

        private readonly ILogger<TelegramBotService> _logger;
        private readonly IServiceProvider _serviceProvider;

        public TelegramBotService(ITelegramBotClient botClient,
        ILogger<TelegramBotService> logger,
        IServiceProvider serviceProvider)
        {
            _botClient = new TelegramBotClient("8095937008:AAEAAzHH49qfJg8cU0JTMDLRr6vCvXMzipA");

            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Telegram Bot Hosted Service started.");

            _botClient.StartReceiving(
      updateHandler: async (client, update, ct) =>
      {
          // Handle update (message, etc.)
          if (update.Message != null)
          {
              await client.SendTextMessageAsync(
                  chatId: update.Message.Chat.Id,
                  text: "Hello from the new StartReceiving method!",
                  cancellationToken: ct
              );
          }
      },
      errorHandler: async (client, exception, ct) =>
      {
          // Log or handle error
          Console.WriteLine($"Bot Error: {exception.Message}");
      },
      receiverOptions: null,
      cancellationToken: cancellationToken
  );


            var me = await _botClient.GetMeAsync(cancellationToken: cancellationToken);
            _logger.LogInformation($"Bot Connected! Username: {me.Username}");
        }


        public async Task HandleUpdateAsync(Update update)
        {
            if (update.Message == null || update.Message.Text == null)
                return;

            var message = update.Message;
            var chatId = message.Chat.Id;
            var text = message.Text;

            if (text.StartsWith("/add"))
            {
                var parts = text.Split(' ');
                if (parts.Length < 3)
                {
                    await _botClient.SendTextMessageAsync(chatId, "Usage: /add <amount> <category> <description (optional)>");
                    return;
                }

                if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal amount))
                {
                    await _botClient.SendTextMessageAsync(chatId, "Invalid amount.");
                    return;
                }

                string category = parts[2];
                string description = parts.Length > 3 ? string.Join(" ", parts.Skip(3)) : "";

                var expense = new Expense
                {
                    UserId = chatId,
                    Amount = amount,
                    Category = category,
                    Description = description,
                    DateAdded = DateTime.UtcNow
                };

                await _expenseService.AddExpenseAsync(expense);
                await _botClient.SendTextMessageAsync(chatId, "✅ Expense added successfully!");
            }
            else if (text.StartsWith("/history"))
            {
                var expenses = await _expenseService.GetExpensesByUserAsync(chatId);
                string response = expenses.Any()
                    ? string.Join("\n", expenses.Select(e => $"{e.DateAdded:MM/dd} - {e.Amount} ({e.Category})"))
                    : "No expenses recorded.";

                await _botClient.SendTextMessageAsync(chatId, response);
            }
            else
            {
                await _botClient.SendTextMessageAsync(chatId, "Commands:\n/add <amount> <category> <description>\n/history - View your expenses");
            }
        }


        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception ex, CancellationToken ct)
        {
            // Log or handle the error
            Console.WriteLine($"Bot Error: {ex.Message}");
            return Task.CompletedTask;
        }


        public async Task SendMessageAsync(long chatId, string message)
        {
            await _botClient.SendTextMessageAsync(chatId, message);
        }
    }
}


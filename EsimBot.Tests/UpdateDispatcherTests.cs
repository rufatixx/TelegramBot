using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using EsimBot.BLL.DTO;
using EsimBot.BLL.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;

namespace EsimBot.Tests;

public sealed class UpdateDispatcherTests
{
    [Fact]
    public async Task LargeBurstHasFixedMemoryAndOneAbusiveChatCannotOccupyOtherSlots()
    {
        var entered = Signal();
        await using var fixture = new Fixture(new Handler(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }));
        var acceptedFromOneChat = Enumerable.Range(1, 10_000).Count(id => fixture.Dispatcher.TryQueueInteractive(Ui(id, 1)));
        Assert.Equal(UpdateDispatcher.PerChatCapacity, acceptedFromOneChat);
        var acceptedOthers = Enumerable.Range(10_001, 50_000).Count(id => fixture.Dispatcher.TryQueueInteractive(Ui(id, id)));
        Assert.Equal(UpdateDispatcher.InteractiveCapacity - UpdateDispatcher.PerChatCapacity, acceptedOthers);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = fixture.Dispatcher.GetSnapshot();
        Assert.Equal(UpdateDispatcher.InteractiveCapacity, snapshot.InteractivePending);
        Assert.Equal(1 + acceptedOthers, snapshot.InteractiveChats);
        Assert.Equal(0, snapshot.FinancialInFlight);
    }

    [Fact]
    public async Task EachChatStaysOrderedWhileAnotherChatContinues()
    {
        var releaseFirst = Signal();
        var firstEntered = Signal();
        var otherCompleted = Signal();
        var allFirstCompleted = Signal();
        var order = new ConcurrentQueue<int>();
        await using var fixture = new Fixture(new Handler(async (update, ct) =>
        {
            if (update.Message!.Chat.Id == 2) { otherCompleted.TrySetResult(); return; }
            order.Enqueue(update.Id);
            if (update.Id == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
            }
            if (update.Id == 4) allFirstCompleted.TrySetResult();
        }));
        for (var id = 1; id <= 4; id++) Assert.True(fixture.Dispatcher.TryQueueInteractive(Ui(id, 1)));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Dispatcher.TryQueueInteractive(Ui(5, 2)));
        await otherCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1 }, order.ToArray());
        releaseFirst.TrySetResult();
        await allFirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2, 3, 4 }, order.ToArray());
    }

    [Fact]
    public async Task FullInteractiveQueueLeavesBothPaymentAndCheckoutCapacityAvailable()
    {
        var priority = new ConcurrentQueue<int>();
        await using var fixture = new Fixture(new Handler(async (update, ct) =>
        {
            if (update.PreCheckoutQuery is not null || update.Message?.SuccessfulPayment is not null)
            {
                priority.Enqueue(update.Id);
                return;
            }
            await Task.Delay(Timeout.Infinite, ct);
        }));
        for (var id = 1; id <= UpdateDispatcher.InteractiveCapacity; id++)
            Assert.True(fixture.Dispatcher.TryQueueInteractive(Ui(id, id)));
        Assert.True(await fixture.Dispatcher.HandlePriorityAsync(Payment(1000), CancellationToken.None));
        Assert.True(await fixture.Dispatcher.HandlePriorityAsync(Checkout(1001), CancellationToken.None));
        Assert.Equal(new[] { 1000, 1001 }, priority.ToArray());
        Assert.Equal(UpdateDispatcher.InteractiveCapacity, fixture.Dispatcher.GetSnapshot().InteractivePending);
    }

    [Fact]
    public async Task FullFinancialSlotsRejectImmediatelyAndDoNotBlockPreCheckout()
    {
        var release = Signal();
        await using var fixture = new Fixture(new Handler(async (update, ct) =>
        {
            if (update.Message?.SuccessfulPayment is not null) await release.Task.WaitAsync(ct);
        }));
        var payments = Enumerable.Range(1, UpdateDispatcher.PriorityCapacity)
            .Select(id => fixture.Dispatcher.HandlePriorityAsync(Payment(id), CancellationToken.None)).ToArray();
        try
        {
            Assert.Equal(UpdateDispatcher.PriorityCapacity, fixture.Dispatcher.GetSnapshot().FinancialInFlight);
            Assert.False(await fixture.Dispatcher.HandlePriorityAsync(Payment(99), CancellationToken.None));
            Assert.True(await fixture.Dispatcher.HandlePriorityAsync(Checkout(100), CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            Assert.All(await Task.WhenAll(payments), Assert.True);
        }
    }

    [Fact]
    public async Task FailedDurablePaymentPropagatesAndReturnsItsSlot()
    {
        await using var fixture = new Fixture(new Handler((_, _) => throw new InvalidOperationException("Persistence failed.")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Dispatcher.HandlePriorityAsync(Payment(1), CancellationToken.None));
        Assert.Equal(0, fixture.Dispatcher.GetSnapshot().FinancialInFlight);
    }

    [Fact]
    public async Task PollingFailureDoesNotAdmitUiOrLaterFinancialUpdates()
    {
        var handled = new ConcurrentQueue<int>();
        await using var fixture = new Fixture(new Handler((update, _) =>
        {
            handled.Enqueue(update.Id);
            return update.Id == 3 ? Task.FromException(new InvalidOperationException("Persistence failed.")) : Task.CompletedTask;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Dispatcher.DispatchPollingBatchAsync(
            [Ui(1, 1), Payment(2, 1), Payment(3, 1), Payment(4, 1)], CancellationToken.None));
        Assert.Equal(new[] { 2, 3 }, handled.ToArray());
        Assert.Equal(0, fixture.Dispatcher.GetSnapshot().InteractivePending);
    }

    [Fact]
    public async Task PollingPaymentsRunAcrossEightChatsWithoutReorderingWithinAChat()
    {
        var waveEntered = Signal();
        var release = Signal();
        var seen = new ConcurrentDictionary<long, ConcurrentQueue<int>>();
        var firstWaveCount = 0;
        await using var fixture = new Fixture(new Handler(async (update, ct) =>
        {
            seen.GetOrAdd(update.Message!.Chat.Id, _ => new ConcurrentQueue<int>()).Enqueue(update.Id);
            if (update.Id <= UpdateDispatcher.PriorityCapacity)
            {
                if (Interlocked.Increment(ref firstWaveCount) == UpdateDispatcher.PriorityCapacity) waveEntered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        }));
        var payments = Enumerable.Range(1, 16).Select(id => Payment(id, (id - 1) % 8 + 1)).ToArray();
        var batch = fixture.Dispatcher.DispatchPollingBatchAsync(payments, CancellationToken.None);
        try
        {
            await waveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, fixture.Dispatcher.GetSnapshot().FinancialInFlight);
            Assert.All(seen.Values, queue => Assert.Single(queue));
        }
        finally { release.TrySetResult(); await batch; }
        Assert.All(seen, chat => Assert.Equal(new[] { (int)chat.Key, (int)chat.Key + 8 }, chat.Value.ToArray()));
    }

    [Fact]
    public async Task PollingRefundWaitsForTheEarlierPaymentOfTheSamePayer()
    {
        var paymentEntered = Signal();
        var release = Signal();
        var seen = new ConcurrentQueue<int>();
        await using var fixture = new Fixture(new Handler(async (update, ct) =>
        {
            seen.Enqueue(update.Id);
            if (update.Message?.SuccessfulPayment is not null)
            {
                paymentEntered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        }));
        var refund = Ui(2, 44);
        refund.Message!.RefundedPayment = new RefundedPayment
        { Currency = "XTR", InvoicePayload = "fake-order", TotalAmount = 1, TelegramPaymentChargeId = "fake-charge-1" };
        var batch = fixture.Dispatcher.DispatchPollingBatchAsync([Payment(1, 44), refund], CancellationToken.None);
        try
        {
            await paymentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1 }, seen.ToArray());
        }
        finally { release.TrySetResult(); await batch; }
        Assert.Equal(new[] { 1, 2 }, seen.ToArray());
    }

    [Fact]
    public async Task PollingPreCheckoutUsesExactlyBoundedParallelism()
    {
        var firstWaveEntered = Signal();
        var release = Signal();
        var count = 0;
        await using var fixture = new Fixture(new Handler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == UpdateDispatcher.PriorityCapacity) firstWaveEntered.TrySetResult();
            await release.Task.WaitAsync(ct);
        }));
        var batch = fixture.Dispatcher.DispatchPollingBatchAsync(Enumerable.Range(1, 20).Select(Checkout).ToArray(), CancellationToken.None);
        try
        {
            await firstWaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(UpdateDispatcher.PriorityCapacity, fixture.Dispatcher.GetSnapshot().PreCheckoutInFlight);
            Assert.Equal(UpdateDispatcher.PriorityCapacity, Volatile.Read(ref count));
        }
        finally { release.TrySetResult(); await batch; }
        Assert.Equal(20, count);
    }

    [Fact]
    public async Task PollingOverflowBusyFeedbackIsDeduplicatedAndBounded()
    {
        var busyEntered = Signal();
        var notices = 0;
        await using var fixture = new Fixture(new Handler((_, ct) => Task.Delay(Timeout.Infinite, ct), async (_, ct) =>
        {
            Interlocked.Increment(ref notices);
            busyEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }));
        await fixture.Dispatcher.DispatchPollingBatchAsync(Enumerable.Range(1, 10_000).Select(id => Ui(id, 1)).ToArray(), CancellationToken.None);
        await busyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref notices));
        Assert.Equal(1, fixture.Dispatcher.GetSnapshot().BusyNoticesPending);
        await fixture.Dispatcher.DispatchPollingBatchAsync(Enumerable.Range(10_001, 4000).Select(id => Ui(id, id)).ToArray(), CancellationToken.None);
        Assert.InRange(fixture.Dispatcher.GetSnapshot().BusyNoticesPending, 1, 64);
        Assert.Equal(UpdateDispatcher.InteractiveCapacity, fixture.Dispatcher.GetSnapshot().InteractivePending);
    }

    [Fact]
    public async Task WebhookOverloadIsRetryableAndFinancialAcknowledgementWaitsForPersistence()
    {
        var persisted = Signal();
        await using var fixture = new Fixture(new Handler(async (update, ct) =>
        {
            if (update.Message?.SuccessfulPayment is not null) await persisted.Task.WaitAsync(ct);
            else await Task.Delay(Timeout.Infinite, ct);
        }));
        for (var id = 1; id <= 4; id++) Assert.True(fixture.Dispatcher.TryQueueInteractive(Ui(id, 1)));
        var webhook = new TelegramWebhookService(Options.Create(new BotOptions { Mode = "Webhook", WebhookSecret = "secret" }),
            new BotRuntimeState { IsReady = true }, NullLogger<TelegramWebhookService>.Instance, dispatcher: fixture.Dispatcher);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await webhook.ReceiveAsync(Context(Ui(99, 1)), CancellationToken.None)).StatusCode);
        var response = webhook.ReceiveAsync(Context(Payment(100)), CancellationToken.None);
        Assert.False(response.IsCompleted);
        persisted.TrySetResult();
        Assert.Equal(StatusCodes.Status200OK, (await response).StatusCode);
    }

    [Fact]
    public async Task WebhookFinancialFailureIsRetryableAndCannotUseTheUiQueue()
    {
        await using var fixture = new Fixture(new Handler((_, _) => throw new InvalidOperationException("Persistence failed.")));
        var webhook = new TelegramWebhookService(Options.Create(new BotOptions { Mode = "Webhook", WebhookSecret = "secret" }),
            new BotRuntimeState { IsReady = true }, NullLogger<TelegramWebhookService>.Instance, dispatcher: fixture.Dispatcher);
        Assert.Throws<ArgumentException>(() => fixture.Dispatcher.TryQueueInteractive(Payment(1)));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await webhook.ReceiveAsync(Context(Payment(1)), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task DispatcherCanRestartAfterWorkerLeaseEnds()
    {
        var handler = new Handler((_, ct) => Task.Delay(Timeout.Infinite, ct));
        var dispatcher = new UpdateDispatcher(handler, NullLogger<UpdateDispatcher>.Instance);
        Assert.False(dispatcher.TryQueueInteractive(Ui(1, 1)));
        for (var run = 0; run < 2; run++)
        {
            using var stop = new CancellationTokenSource();
            var task = dispatcher.RunAsync(stop.Token);
            Assert.True(dispatcher.TryQueueInteractive(Ui(1, 1)));
            stop.Cancel();
            try { await task; } catch (OperationCanceledException) { }
            Assert.Equal(0, dispatcher.GetSnapshot().InteractivePending);
            Assert.False(dispatcher.TryQueueInteractive(Ui(2, 2)));
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Update Ui(int id, long chatId) => new()
    {
        Id = id, Message = new Message { Id = id, Chat = new Chat { Id = chatId, Type = ChatType.Private }, Text = "/start" }
    };
    private static Update Payment(int id, long? chatId = null)
    {
        var update = Ui(id, chatId ?? id);
        update.Message!.SuccessfulPayment = new SuccessfulPayment
        { Currency = "XTR", InvoicePayload = "fake-order", TelegramPaymentChargeId = $"fake-charge-{id}", ProviderPaymentChargeId = "", TotalAmount = 1 };
        return update;
    }
    private static Update Checkout(int id) => new()
    {
        Id = id, PreCheckoutQuery = new PreCheckoutQuery
        { Id = $"fake-checkout-{id}", From = new User { Id = id, FirstName = "Test" }, Currency = "XTR", InvoicePayload = "fake-order", TotalAmount = 1 }
    };
    private static HttpContext Context(Update update)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = "secret";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(update, JsonBotAPI.Options)));
        return context;
    }

    private sealed class Handler(Func<Update, CancellationToken, Task> action,
        Func<Update, CancellationToken, Task>? notify = null) : IBotHandler
    {
        public Task HandleAsync(Update update, CancellationToken ct) => action(update, ct);
        public Task NotifyBusyAsync(Update update, CancellationToken ct) => notify?.Invoke(update, ct) ?? Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _running;
        public UpdateDispatcher Dispatcher { get; }
        public Fixture(IBotHandler handler)
        {
            Dispatcher = new UpdateDispatcher(handler, NullLogger<UpdateDispatcher>.Instance);
            _running = Dispatcher.RunAsync(_stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}

using System.Diagnostics;
using System.Threading.Channels;
using EsimBot.BLL.DTO;
using Telegram.Bot.Types;

namespace EsimBot.BLL.Services;

public sealed class UpdateDispatcher(IBotHandler handler, ILogger<UpdateDispatcher> logger) : IUpdateDispatcher
{
    public const int InteractiveCapacity = 256;
    public const int PerChatCapacity = 4;
    public const int InteractiveWorkers = 12;
    public const int PriorityCapacity = 8;
    private const int BusyCapacity = 64;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _financial = new(PriorityCapacity, PriorityCapacity);
    private readonly SemaphoreSlim _preCheckout = new(PriorityCapacity, PriorityCapacity);
    private Session? _session;

    public async Task RunAsync(CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new Session();
        lock (_gate)
        {
            if (_session is not null) throw new InvalidOperationException("Update dispatcher is already running.");
            _session = session;
        }
        var workers = Enumerable.Range(0, InteractiveWorkers).Select(_ => ProcessInteractiveAsync(session, run.Token))
            .Concat(Enumerable.Range(0, 2).Select(_ => ProcessBusyNoticesAsync(session, run.Token))).ToArray();
        try
        {
            await await Task.WhenAny(workers);
        }
        finally
        {
            run.Cancel();
            lock (_gate)
            {
                if (ReferenceEquals(_session, session)) _session = null;
                session.Ready.Writer.TryComplete();
                session.Busy.Writer.TryComplete();
            }
            try { await Task.WhenAll(workers); }
            catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        }
    }

    public bool TryQueueInteractive(Update update)
    {
        if (IsPriority(update)) throw new ArgumentException("Financial updates require durable handling.", nameof(update));
        var chatId = ChatId(update);
        if (chatId is null) return true;
        lock (_gate)
        {
            var session = _session;
            if (session is null) return false;
            session.Chats.TryGetValue(chatId.Value, out var chat);
            if (session.Outstanding >= InteractiveCapacity || chat?.Outstanding >= PerChatCapacity) return false;
            if (chat is null)
            {
                chat = new ChatQueue();
                session.Chats.Add(chatId.Value, chat);
                // Exactly one ready entry or executing worker exists for each non-empty chat.
                if (!session.Ready.Writer.TryWrite(chatId.Value))
                    throw new InvalidOperationException("Interactive scheduler capacity invariant failed.");
            }
            chat.Updates.Enqueue(update);
            chat.Outstanding++;
            session.Outstanding++;
            return true;
        }
    }

    public async Task<bool> HandlePriorityAsync(Update update, CancellationToken ct)
    {
        if (!IsPriority(update)) throw new ArgumentException("Update is not a payment or pre-checkout.", nameof(update));
        // Independent admission means slow catalogue work cannot exhaust payment/pre-checkout capacity.
        // There is no unbounded semaphore wait queue: webhook overload receives a retryable 503.
        var slots = update.PreCheckoutQuery is not null ? _preCheckout : _financial;
        if (!await slots.WaitAsync(0, ct)) return false;
        try
        {
            await handler.HandleAsync(update, ct);
            return true;
        }
        finally { slots.Release(); }
    }

    public async Task DispatchPollingBatchAsync(IReadOnlyCollection<Update> updates, CancellationToken ct)
    {
        await Parallel.ForEachAsync(updates.Where(update => update.PreCheckoutQuery is not null),
            new ParallelOptions { MaxDegreeOfParallelism = PriorityCapacity, CancellationToken = ct },
            async (update, token) =>
            {
                if (!await HandlePriorityAsync(update, token))
                    throw new InvalidOperationException("Pre-checkout capacity unavailable; polling batch will retry.");
            });
        var financialChats = updates.Where(update => update.PreCheckoutQuery is null && IsPriority(update))
            .GroupBy(update => update.Message!.Chat.Id);
        await Parallel.ForEachAsync(financialChats,
            new ParallelOptions { MaxDegreeOfParallelism = PriorityCapacity, CancellationToken = ct },
            async (chat, token) =>
            {
                // Preserve payment -> refund order for a payer while unrelated payers proceed concurrently.
                foreach (var update in chat)
                    if (!await HandlePriorityAsync(update, token))
                        throw new InvalidOperationException("Payment capacity unavailable; polling batch will retry.");
            });
        // UI is admitted only after every financial event succeeded. The caller then persists one offset.
        foreach (var update in updates.Where(update => !IsPriority(update)))
            if (!TryQueueInteractive(update)) QueueBusyNotice(update);
    }

    public UpdateDispatcherSnapshot GetSnapshot()
    {
        lock (_gate)
            return new(_session?.Outstanding ?? 0, _session?.Chats.Count ?? 0, _session?.BusyOutstanding ?? 0,
                PriorityCapacity - _financial.CurrentCount, PriorityCapacity - _preCheckout.CurrentCount);
    }

    private async Task ProcessInteractiveAsync(Session session, CancellationToken ct)
    {
        await foreach (var chatId in session.Ready.Reader.ReadAllAsync(ct))
        {
            Update update;
            lock (_gate) update = session.Chats[chatId].Updates.Dequeue();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await handler.HandleAsync(update, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Interactive action failed ({ErrorType})", ex.GetType().Name);
                // The handler owns accepted callback acknowledgements and error replies.
                // Busy feedback is only for updates rejected before handling, avoiding a second callback ACK.
            }
            finally
            {
                lock (_gate)
                {
                    var chat = session.Chats[chatId];
                    chat.Outstanding--;
                    session.Outstanding--;
                    if (chat.Outstanding == 0) session.Chats.Remove(chatId);
                    else if (!ct.IsCancellationRequested && !session.Ready.Writer.TryWrite(chatId))
                        throw new InvalidOperationException("Interactive scheduler capacity invariant failed.");
                }
            }
        }
    }

    private void QueueBusyNotice(Update update)
    {
        var chatId = ChatId(update);
        if (chatId is null) return;
        lock (_gate)
        {
            var session = _session;
            if (session is null || session.BusyOutstanding >= BusyCapacity) return;
            if (session.LastBusy.TryGetValue(chatId.Value, out var previous)
                && Stopwatch.GetElapsedTime(previous) < TimeSpan.FromSeconds(5)) return;
            if (!session.Busy.Writer.TryWrite(update)) return;
            session.BusyOutstanding++;
            if (session.LastBusy.Count >= InteractiveCapacity && !session.LastBusy.ContainsKey(chatId.Value))
                session.LastBusy.Remove(session.LastBusy.MinBy(entry => entry.Value).Key);
            session.LastBusy[chatId.Value] = Stopwatch.GetTimestamp();
        }
    }

    private async Task ProcessBusyNoticesAsync(Session session, CancellationToken ct)
    {
        await foreach (var update in session.Busy.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await handler.NotifyBusyAsync(update, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogDebug("Busy notice could not be delivered ({ErrorType})", ex.GetType().Name); }
            finally { lock (_gate) session.BusyOutstanding--; }
        }
    }

    private static bool IsPriority(Update update) => update.PreCheckoutQuery is not null
        || update.Message?.SuccessfulPayment is not null || update.Message?.RefundedPayment is not null;

    private static long? ChatId(Update update) => update.CallbackQuery?.Message?.Chat.Id ?? update.Message?.Chat.Id;

    private sealed class ChatQueue
    {
        public Queue<Update> Updates { get; } = new();
        public int Outstanding { get; set; }
    }

    private sealed class Session
    {
        public Dictionary<long, ChatQueue> Chats { get; } = new();
        public Dictionary<long, long> LastBusy { get; } = new();
        public Channel<long> Ready { get; } = Channel.CreateBounded<long>(new BoundedChannelOptions(InteractiveCapacity)
        { SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        public Channel<Update> Busy { get; } = Channel.CreateBounded<Update>(new BoundedChannelOptions(BusyCapacity)
        { SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        public int Outstanding { get; set; }
        public int BusyOutstanding { get; set; }
    }
}

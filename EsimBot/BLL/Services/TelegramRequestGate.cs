namespace EsimBot.BLL.Services;

/// <summary>Bounds and paces outgoing messages without delaying Telegram control and payment answers.</summary>
public sealed class TelegramRequestGate(TimeProvider? timeProvider = null) : ITelegramRequestGate
{
    private static readonly TimeSpan GlobalInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan ChatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(10);
    private const int MaximumChats = 256;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _admissions = new(128, 128);
    private readonly SemaphoreSlim _global = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<long, ChatState> _chats = new();
    private long? _lastGlobalStart;

    public async ValueTask<IDisposable> AcquireAsync(long chatId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_admissions.Wait(0)) throw Busy();
        ChatState? chat = null;
        var acquired = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueueTimeout);
        try
        {
            lock (_sync)
            {
                // Retain the cooldown, but never retain a permanent entry for every historical customer.
                var expired = _chats.Where(pair => pair.Value.Users == 0
                    && (pair.Value.LastStart is null || _clock.GetElapsedTime(pair.Value.LastStart.Value) >= ChatInterval))
                    .Select(pair => pair.Key).ToArray();
                foreach (var key in expired) _chats.Remove(key);
                if (!_chats.TryGetValue(chatId, out chat))
                {
                    if (_chats.Count >= MaximumChats) throw Busy();
                    chat = new ChatState();
                    _chats.Add(chatId, chat);
                }
                chat.Users++;
            }
            await chat.Mutex.WaitAsync(timeout.Token);
            acquired = true;
            if (chat.LastStart is { } lastChatStart)
            {
                var delay = ChatInterval - _clock.GetElapsedTime(lastChatStart);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _clock, timeout.Token);
            }
            await _global.WaitAsync(timeout.Token);
            try
            {
                if (_lastGlobalStart is { } lastGlobalStart)
                {
                    var delay = GlobalInterval - _clock.GetElapsedTime(lastGlobalStart);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, _clock, timeout.Token);
                }
                _lastGlobalStart = _clock.GetTimestamp();
                chat.LastStart = _lastGlobalStart;
            }
            finally { _global.Release(); }
            return new Lease(this, chat);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Release(chat, acquired);
            throw Busy();
        }
        catch
        {
            Release(chat, acquired);
            throw;
        }
    }

    private void Release(ChatState? chat, bool acquired)
    {
        if (acquired) chat!.Mutex.Release();
        if (chat is not null)
        {
            lock (_sync) chat.Users--;
        }
        _admissions.Release();
    }

    private static InvalidOperationException Busy() => new("Telegram message capacity is temporarily exhausted.");

    private sealed class ChatState
    {
        public readonly SemaphoreSlim Mutex = new(1, 1);
        public long? LastStart;
        public int Users;
    }

    private sealed class Lease(TelegramRequestGate owner, ChatState chat) : IDisposable
    {
        private TelegramRequestGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(chat, true);
    }
}

namespace EsimBot.BLL.Services;

public sealed class BotRuntimeState : IBotRuntimeState
{
    private int _ready;
    public bool IsReady { get => Volatile.Read(ref _ready) == 1; set => Volatile.Write(ref _ready, value ? 1 : 0); }
}

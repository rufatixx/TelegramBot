namespace EsimBot.BLL.Services;

public interface IBotRuntimeState
{
    bool IsReady { get; set; }
}

namespace EsimBot.BLL.DTO;

public sealed record UpdateDispatcherSnapshot(int InteractivePending, int InteractiveChats,
    int BusyNoticesPending, int FinancialInFlight, int PreCheckoutInFlight);

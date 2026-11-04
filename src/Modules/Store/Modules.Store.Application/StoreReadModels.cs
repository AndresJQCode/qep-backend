namespace Modules.Store.Application;

public enum StoreInboxView
{
    Untaken,
    TakenByMe,
    All,
}

public sealed record StoreInboxFilter(Guid TenantId, StoreInboxView View, Guid CallerMemberId, int Page, int PageSize);

public sealed record StoreInboxCounts(int Untaken, int TakenByMe, int ReceivedToday);

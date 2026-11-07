namespace Modules.Store.Application;

public enum StoreInboxView
{
    Untaken,
    TakenByMe,
    All,
}

public sealed record StoreInboxFilter(Guid TenantId, StoreInboxView View, Guid CallerMemberId, int Page, int PageSize)
{
    /// <summary>Con el tope de página de 100, la posición más lejana (1 000 000) cabe de sobra en un int; sin él, <c>(page - 1) * pageSize</c> se desborda y Postgres rechaza el OFFSET negativo.</summary>
    public const int MaxPage = 10_000;

    public static int ClampPage(int page) => Math.Clamp(page, 1, MaxPage);

    /// <summary>Cuántas filas saltar: siempre de la página ya acotada.</summary>
    public int Skip => (ClampPage(Page) - 1) * PageSize;
}

public sealed record StoreInboxCounts(int Untaken, int TakenByMe, int ReceivedToday);

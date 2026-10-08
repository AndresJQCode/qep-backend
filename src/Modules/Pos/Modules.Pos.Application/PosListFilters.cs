using Modules.Pos.Domain;

namespace Modules.Pos.Application;

/// <param name="Cashier">null = todas (pos.register.read); si no, sólo las del cajero llamador.</param>
/// <param name="OpenedFromUtc">Inicio del día local del tenant, ya en UTC.</param>
/// <param name="OpenedToUtc">Fin exclusivo del día local del tenant, ya en UTC.</param>
public sealed record CashSessionFilter(
    Guid TenantId,
    MemberId? Cashier,
    DateTimeOffset? OpenedFromUtc,
    DateTimeOffset? OpenedToUtc,
    CashSessionStatus? Status,
    int Page,
    int PageSize);

public sealed record PosSaleFilter(
    Guid TenantId,
    MemberId? Cashier,
    CashSessionId? SessionId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    PosSaleStatus? Status,
    string? Number,
    int Page,
    int PageSize);

/// <summary>Venta con lo que la lista necesita de su caja, resuelto en lote (no una consulta por fila).</summary>
public sealed record PosSaleListRow(PosSale Sale, string CashierName, CashSessionStatus SessionStatus);

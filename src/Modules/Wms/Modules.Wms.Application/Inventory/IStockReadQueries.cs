using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

/// <summary><c>Offset</c> es <c>long</c>: <c>(page - 1) * pageSize</c> con un <c>page</c> enorme desbordaría un <c>int</c>.</summary>
public sealed record StockFilter(
    Guid TenantId,
    Guid? WarehouseId,
    Guid? OwnerId,
    Guid? VariantId,
    Guid? LocationId,
    Guid? LotId,
    StockStatus? Status,
    bool OnlyAvailable,
    bool IncludeVirtual,
    string? Search,
    long Offset,
    int Limit);

/// <summary>Una fila de <c>wms.v_stock_rows</c> con el nombre del dueño, el código de la bodega y los datos del lote. Nada de Catalog:
/// el producto, su código y el código de barras los pega el handler.</summary>
public sealed record StockBalanceRow(
    Guid OwnerId,
    string OwnerName,
    Guid WarehouseId,
    string WarehouseCode,
    Guid LocationId,
    string LocationCode,
    string LocationPath,
    Guid VariantId,
    Guid? LotId,
    string? LotCode,
    DateOnly? ExpirationDate,
    string Status,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    decimal Pickable,
    DateTimeOffset UpdatedAt);

public sealed record StockSearchResult(IReadOnlyList<StockBalanceRow> Rows, int Total, StockTotalsDto Totals);

/// <summary>Las siete columnas de <c>wms.v_stock_summary</c> sumadas sobre el filtro. Las fórmulas de <c>available</c>,
/// <c>pickable</c>, <c>received</c> y <c>blocked</c> viven en la vista y no se repiten en C#.</summary>
public sealed record StockSummaryRow(
    decimal OnHand,
    decimal Received,
    decimal Available,
    decimal Pickable,
    decimal Reserved,
    decimal Blocked,
    decimal InTransit);

public sealed record MovementFilter(
    Guid TenantId,
    Guid? VariantId,
    Guid? LocationId,
    Guid? LotId,
    string? Serial,
    string? DocumentType,
    Guid? DocumentId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    MovementCursor? After,
    int Limit);

public sealed record MovementRow(
    Guid Id,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string Reason,
    decimal Quantity,
    string? CapturedUom,
    decimal? CapturedQty,
    decimal? ConversionFactor,
    string FromLocationCode,
    string FromStatus,
    string ToLocationCode,
    string ToStatus,
    Guid VariantId,
    string? LotCode,
    string? Serial,
    string? DocumentType,
    Guid? DocumentId,
    Guid BatchId,
    Guid? ReversesMovementId,
    Guid ActorId,
    string ActorType,
    string? DeviceId);

/// <summary>Hasta <c>Limit</c> filas y si había más: la implementación pide <c>Limit + 1</c> y descarta la extra, que sólo decide el
/// cursor siguiente.</summary>
public sealed record MovementSearchResult(IReadOnlyList<MovementRow> Rows, bool HasMore);

/// <summary>Lecturas de saldos e historia (RF-021, RF-022): SQL crudo sobre las vistas y el ledger, sin rastreo.</summary>
public interface IStockReadQueries
{
    Task<StockSearchResult> SearchAsync(StockFilter filter, CancellationToken cancellationToken);

    Task<StockSummaryRow> SummarizeAsync(Guid tenantId, Guid? warehouseId, Guid? ownerId, Guid? variantId, CancellationToken cancellationToken);

    Task<MovementSearchResult> ListMovementsAsync(MovementFilter filter, CancellationToken cancellationToken);

    /// <summary>El saldo físico de exactamente esa clave, sin unidad logística: lote nulo es «sin lote», no «cualquiera»
    /// (<c>lot_id IS NOT DISTINCT FROM</c>). Cero si no hay fila. Lo que el conteo ciego compara contra lo que el operario contó.</summary>
    Task<decimal> FindOnHandAsync(Guid tenantId, StockKey key, CancellationToken cancellationToken);

    /// <summary>Bloquea (<c>SELECT … FOR UPDATE</c>) las filas de saldo suelto de esas claves, <b>en el orden de
    /// <c>StockKeyComparer</c></b>, el mismo con que el ledger toma sus bloqueos: así nadie puede cambiarlas entre una lectura de
    /// <see cref="FindOnHandAsync"/> y el movimiento que le sigue. Exige la transacción abierta, y quien la llama no puede tomar después
    /// <c>count-key:</c> ni numeraciones (orden de bloqueos: saldos al final). La clave sin fila no bloquea nada.</summary>
    Task LockBalancesAsync(Guid tenantId, IReadOnlyCollection<StockKey> keys, CancellationToken cancellationToken);

    /// <summary>Las claves <c>(variante, ubicación, lote)</c> de ese dueño que ya tienen saldo distinto de cero en algún estado: la carga
    /// inicial las bloquea para no duplicarlo. Sólo mira las variantes y ubicaciones que se le pasan.</summary>
    Task<IReadOnlySet<(Guid VariantId, Guid LocationId, Guid? LotId)>> FindNonZeroBalanceKeysAsync(
        Guid tenantId,
        Guid ownerId,
        IReadOnlyCollection<Guid> variantIds,
        IReadOnlyCollection<Guid> locationIds,
        CancellationToken cancellationToken);
}

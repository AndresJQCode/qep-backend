using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

/// <summary>Lee un movimiento del ledger para compensarlo. Sólo lectura sin rastreo: el ledger sigue siendo el único escritor.</summary>
public interface IMovementReader
{
    /// <summary>Por <c>id</c> (la PK <c>(id, occurred_at)</c> da índice por prefijo) y acotado al tenant.</summary>
    Task<StockMovement?> GetAsync(Guid tenantId, Guid movementId, CancellationToken cancellationToken);

    /// <summary>Si ya hay un compensatorio que apunta a este movimiento (<c>IX_stock_movements_reverses</c>).</summary>
    Task<bool> IsReversedAsync(Guid tenantId, Guid movementId, CancellationToken cancellationToken);

    /// <summary>Los movimientos de un lote que no son compensaciones, con su <c>occurred_at</c> (la tabla está particionada por mes: sin
    /// la fecha, la consulta recorre todas las particiones). Lo usa el inventario básico para deshacer exactamente lo que asentó.</summary>
    Task<IReadOnlyList<StockMovement>> ListBatchAsync(Guid tenantId, Guid batchId, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}

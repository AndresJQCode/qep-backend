namespace Modules.Wms.Application;

/// <summary>La conciliación del ledger contra los saldos (RF-029, RNF-01). Sólo registra: una diferencia queda en
/// <c>wms.balance_checks</c> para que una persona decida, y los saldos no se tocan.</summary>
public interface IBalanceReconciliation
{
    /// <summary>Las bodegas a conciliar, agrupadas por tenant y en orden estable.</summary>
    Task<IReadOnlyList<Guid>> ListWarehousesAsync(CancellationToken cancellationToken);

    /// <summary>Recompone <c>on_hand</c> desde el ledger para esa bodega, por tandas de variantes, y registra una fila por cada clave
    /// que difiere del saldo. Devuelve cuántas claves tenían diferencia.</summary>
    Task<int> ReconcileWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken);
}

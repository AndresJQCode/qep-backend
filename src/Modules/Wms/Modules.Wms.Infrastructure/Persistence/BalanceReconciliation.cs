using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class BalanceReconciliation(WmsDbContext dbContext) : IBalanceReconciliation
{
    // Variantes por tanda: acota lo que una sola sentencia agrega del ledger (la bodega entera de un tenant grande no cabe en una).
    private const int VariantsPerBatch = 500;

    // El mayor uuid posible: PostgreSQL ordena por bytes, igual que las cotas que él mismo devuelve.
    private static readonly Guid MaxUuid = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public async Task<IReadOnlyList<Guid>> ListWarehousesAsync(CancellationToken cancellationToken) =>
        await dbContext.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM wms.warehouses ORDER BY tenant_id, id")
            .ToListAsync(cancellationToken);

    public async Task<int> ReconcileWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var drifts = 0;
        var after = Guid.Empty;
        while (true)
        {
            var upper = await UpperBoundAsync(warehouseId, after, cancellationToken);
            drifts += await CheckBatchAsync(warehouseId, after, upper ?? MaxUuid, cancellationToken);
            if (upper is null)
            {
                return drifts;
            }

            after = upper.Value;
        }
    }

    // La variante número VariantsPerBatch (en orden) de las que tienen saldo o movimiento en la bodega, después de `after`. Null si ya
    // no quedan tantas: la última tanda va hasta el final.
    private async Task<Guid?> UpperBoundAsync(Guid warehouseId, Guid after, CancellationToken cancellationToken)
    {
        var offset = VariantsPerBatch - 1;
        var bounds = await dbContext.Database.SqlQuery<Guid>(
            $"""
            SELECT v AS "Value" FROM (
                SELECT variant_id AS v FROM wms.stock_balances WHERE warehouse_id = {warehouseId} AND variant_id > {after}
                UNION
                SELECT m.variant_id FROM wms.stock_movements m
                    JOIN wms.locations l ON l.id = m.to_location_id
                    WHERE l.warehouse_id = {warehouseId} AND m.variant_id > {after}
                UNION
                SELECT m.variant_id FROM wms.stock_movements m
                    JOIN wms.locations l ON l.id = m.from_location_id
                    WHERE l.warehouse_id = {warehouseId} AND m.variant_id > {after}) variants
            ORDER BY v
            LIMIT 1 OFFSET {offset}
            """)
            .ToListAsync(cancellationToken);
        return bounds.Count == 0 ? null : bounds[0];
    }

    // Una sola sentencia lee el ledger y los saldos con el mismo snapshot: movimiento y saldo commitean juntos, así que una escritura
    // concurrente no fabrica diferencias. No corrige nada; last_movement_id queda nulo porque la fila describe una clave, no un asiento.
    private async Task<int> CheckBatchAsync(Guid warehouseId, Guid after, Guid upper, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO wms.balance_checks
                (id, tenant_id, warehouse_id, owner_id, location_id, variant_id, lot_id, status, ledger_on_hand, balance_on_hand, last_movement_id, checked_at)
            SELECT gen_random_uuid(), coalesce(ledger.tenant_id, bal.tenant_id), {warehouseId}, coalesce(ledger.owner_id, bal.owner_id),
                   coalesce(ledger.location_id, bal.location_id), coalesce(ledger.variant_id, bal.variant_id),
                   coalesce(ledger.lot_id, bal.lot_id), coalesce(ledger.status, bal.status),
                   coalesce(ledger.on_hand, 0), coalesce(bal.on_hand, 0), NULL, now()
            FROM (
                SELECT movement.tenant_id, movement.owner_id, movement.location_id, movement.variant_id, movement.lot_id, movement.status,
                       sum(movement.delta) AS on_hand
                FROM (
                    SELECT m.tenant_id, m.owner_id, m.to_location_id AS location_id, m.variant_id, m.lot_id, m.to_status AS status,
                           m.quantity AS delta
                    FROM wms.stock_movements m JOIN wms.locations l ON l.id = m.to_location_id
                    WHERE l.warehouse_id = {warehouseId} AND m.variant_id > {after} AND m.variant_id <= {upper}
                    UNION ALL
                    SELECT m.tenant_id, m.owner_id, m.from_location_id, m.variant_id, m.lot_id, m.from_status, -m.quantity
                    FROM wms.stock_movements m JOIN wms.locations l ON l.id = m.from_location_id
                    WHERE l.warehouse_id = {warehouseId} AND m.variant_id > {after} AND m.variant_id <= {upper}) movement
                GROUP BY movement.tenant_id, movement.owner_id, movement.location_id, movement.variant_id, movement.lot_id, movement.status) ledger
            FULL OUTER JOIN (
                SELECT tenant_id, owner_id, location_id, variant_id, lot_id, status, on_hand
                FROM wms.stock_balances
                WHERE warehouse_id = {warehouseId} AND variant_id > {after} AND variant_id <= {upper}) bal
              ON bal.tenant_id = ledger.tenant_id AND bal.owner_id = ledger.owner_id AND bal.location_id = ledger.location_id
             AND bal.variant_id = ledger.variant_id AND bal.lot_id IS NOT DISTINCT FROM ledger.lot_id AND bal.status = ledger.status
            WHERE coalesce(ledger.on_hand, 0) <> coalesce(bal.on_hand, 0)
            """,
            cancellationToken);
}

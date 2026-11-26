using System.Data;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class BalanceReconciliation(WmsDbContext dbContext) : IBalanceReconciliation
{
    // Variantes por tanda: acota lo que una sola sentencia agrega del ledger (la bodega entera de un tenant grande no cabe en una).
    private const int VariantsPerBatch = 500;

    // El timeout por defecto de Npgsql (30 s) no alcanza para agregar el ledger de una bodega grande; esta tanda corre de noche y sin
    // nadie esperando, así que se le da holgura en vez de fallar cada madrugada.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    // El mayor uuid posible: PostgreSQL ordena por bytes, igual que las cotas que él mismo devuelve.
    private static readonly Guid MaxUuid = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public async Task<IReadOnlyList<Guid>> ListWarehousesAsync(CancellationToken cancellationToken) =>
        await dbContext.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM wms.warehouses ORDER BY tenant_id, id")
            .ToListAsync(cancellationToken);

    public async Task<int> ReconcileWarehouseAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        dbContext.Database.SetCommandTimeout(CommandTimeout);

        // El tenant sale de la bodega y entra a toda consulta del ledger: sin él no sirve el índice (tenant, variante, fecha).
        var tenants = await dbContext.Database.SqlQuery<Guid>($"SELECT tenant_id AS \"Value\" FROM wms.warehouses WHERE id = {warehouseId}")
            .ToListAsync(cancellationToken);
        if (tenants.Count == 0)
        {
            return 0;
        }

        var tenantId = tenants[0];
        var drifts = 0;
        var after = Guid.Empty;
        foreach (var upper in (await BatchBoundsAsync(tenantId, warehouseId, cancellationToken)).Append(MaxUuid))
        {
            drifts += await CheckBatchAsync(tenantId, warehouseId, after, upper, cancellationToken);
            after = upper;
        }

        return drifts;
    }

    // Una sola pasada por las variantes de la bodega, en vez de una por tanda: devuelve la variante número 500, 1000, ... en orden,
    // y cada tanda es (cota anterior, cota]. La última, hasta el final, la agrega quien llama.
    private async Task<IReadOnlyList<Guid>> BatchBoundsAsync(Guid tenantId, Guid warehouseId, CancellationToken cancellationToken) =>
        await dbContext.Database.SqlQuery<Guid>(
            $"""
            SELECT numbered.v AS "Value" FROM (
                SELECT variants.v, row_number() OVER (ORDER BY variants.v) AS position FROM (
                    SELECT variant_id AS v FROM wms.stock_balances WHERE tenant_id = {tenantId} AND warehouse_id = {warehouseId}
                    UNION
                    SELECT m.variant_id FROM wms.stock_movements m
                        JOIN wms.locations l ON l.id = m.to_location_id
                        WHERE m.tenant_id = {tenantId} AND l.warehouse_id = {warehouseId}
                    UNION
                    SELECT m.variant_id FROM wms.stock_movements m
                        JOIN wms.locations l ON l.id = m.from_location_id
                        WHERE m.tenant_id = {tenantId} AND l.warehouse_id = {warehouseId}) variants) numbered
            WHERE numbered.position % {VariantsPerBatch} = 0
            ORDER BY numbered.v
            """)
            .ToListAsync(cancellationToken);

    // El NOT EXISTS de la tanda no alcanza entre pods: dos pasadas a la vez no ven la fila sin confirmar de la otra y la insertan dos
    // veces. El candado por bodega las pone en fila. Va en su propia sentencia y antes de la tanda, en READ COMMITTED: así la tanda toma
    // su snapshot ya con el candado y ve lo que confirmó el otro pod. Una transacción por tanda, para no retenerlo toda la bodega.
    private async Task<int> CheckBatchAsync(Guid tenantId, Guid warehouseId, Guid after, Guid upper, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockKey = "balance-check:" + warehouseId.ToString("D");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({WmsUnitOfWork.WmsLockNamespace}, hashtext({lockKey}))", cancellationToken);

        var drifts = await RecordDriftAsync(tenantId, warehouseId, after, upper, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return drifts;
    }

    // Una sola sentencia lee el ledger y los saldos con el mismo snapshot: movimiento y saldo commitean juntos, así que una escritura
    // concurrente no fabrica diferencias. No corrige nada. Devuelve cuántas claves difieren, NO cuántas filas insertó: una repetición el
    // mismo día no duplica la fila (dedupe por clave y día UTC) pero la diferencia sigue ahí, y el gauge no debe apagarse.
    private async Task<int> RecordDriftAsync(Guid tenantId, Guid warehouseId, Guid after, Guid upper, CancellationToken cancellationToken) =>
        (await dbContext.Database.SqlQuery<int>(
            $"""
            WITH drift AS (
                SELECT coalesce(ledger.tenant_id, bal.tenant_id) AS tenant_id, coalesce(ledger.owner_id, bal.owner_id) AS owner_id,
                       coalesce(ledger.location_id, bal.location_id) AS location_id, coalesce(ledger.variant_id, bal.variant_id) AS variant_id,
                       coalesce(ledger.lot_id, bal.lot_id) AS lot_id, coalesce(ledger.handling_unit_id, bal.handling_unit_id) AS handling_unit_id,
                       coalesce(ledger.status, bal.status) AS status,
                       coalesce(ledger.on_hand, 0) AS ledger_on_hand, coalesce(bal.on_hand, 0) AS balance_on_hand,
                       ledger.last_movement_id
                FROM (
                    SELECT movement.tenant_id, movement.owner_id, movement.location_id, movement.variant_id, movement.lot_id,
                           movement.handling_unit_id, movement.status, sum(movement.delta) AS on_hand,
                           (array_agg(movement.id ORDER BY movement.occurred_at DESC, movement.id DESC))[1] AS last_movement_id
                    FROM (
                        SELECT m.id, m.occurred_at, m.tenant_id, m.owner_id, m.to_location_id AS location_id, m.variant_id, m.lot_id,
                               m.to_handling_unit_id AS handling_unit_id, m.to_status AS status, m.quantity AS delta
                        FROM wms.stock_movements m JOIN wms.locations l ON l.id = m.to_location_id
                        WHERE m.tenant_id = {tenantId} AND l.warehouse_id = {warehouseId} AND m.variant_id > {after} AND m.variant_id <= {upper}
                        UNION ALL
                        SELECT m.id, m.occurred_at, m.tenant_id, m.owner_id, m.from_location_id, m.variant_id, m.lot_id,
                               m.from_handling_unit_id, m.from_status, -m.quantity
                        FROM wms.stock_movements m JOIN wms.locations l ON l.id = m.from_location_id
                        WHERE m.tenant_id = {tenantId} AND l.warehouse_id = {warehouseId} AND m.variant_id > {after} AND m.variant_id <= {upper}) movement
                    GROUP BY movement.tenant_id, movement.owner_id, movement.location_id, movement.variant_id, movement.lot_id,
                             movement.handling_unit_id, movement.status) ledger
                FULL OUTER JOIN (
                    SELECT tenant_id, owner_id, location_id, variant_id, lot_id, handling_unit_id, status, on_hand
                    FROM wms.stock_balances
                    WHERE tenant_id = {tenantId} AND warehouse_id = {warehouseId} AND variant_id > {after} AND variant_id <= {upper}) bal
                  ON bal.tenant_id = ledger.tenant_id AND bal.owner_id = ledger.owner_id AND bal.location_id = ledger.location_id
                 AND bal.variant_id = ledger.variant_id AND bal.lot_id IS NOT DISTINCT FROM ledger.lot_id
                 AND bal.handling_unit_id IS NOT DISTINCT FROM ledger.handling_unit_id AND bal.status = ledger.status
                WHERE coalesce(ledger.on_hand, 0) <> coalesce(bal.on_hand, 0)),
            recorded AS (
                INSERT INTO wms.balance_checks
                    (id, tenant_id, warehouse_id, owner_id, location_id, variant_id, lot_id, handling_unit_id, status, ledger_on_hand,
                     balance_on_hand, last_movement_id, checked_at)
                SELECT gen_random_uuid(), d.tenant_id, {warehouseId}, d.owner_id, d.location_id, d.variant_id, d.lot_id, d.handling_unit_id,
                       d.status,
                       d.ledger_on_hand, d.balance_on_hand, d.last_movement_id, now()
                FROM drift d
                WHERE NOT EXISTS (
                    SELECT 1 FROM wms.balance_checks c
                    WHERE c.tenant_id = d.tenant_id AND c.warehouse_id = {warehouseId} AND c.owner_id = d.owner_id
                      AND c.location_id = d.location_id AND c.variant_id = d.variant_id AND c.lot_id IS NOT DISTINCT FROM d.lot_id
                      AND c.handling_unit_id IS NOT DISTINCT FROM d.handling_unit_id
                      AND c.status = d.status AND c.checked_at >= (date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'))
                RETURNING 1)
            SELECT count(*)::int AS "Value" FROM drift
            """)
            .ToListAsync(cancellationToken)).Single();
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class BalanceChangeQueue(WmsDbContext dbContext) : IBalanceChangeQueue
{
    private const int ColumnsPerKey = 4;

    // Un solo INSERT, ordenado (S26). Con un INSERT por clave en orden de aparición, dos lotes cruzados tomarían los bloqueos de la
    // cola en orden opuesto y reabrirían el deadlock que el orden de StockKeyComparer cierra en los saldos.
    // DO UPDATE y no DO NOTHING: DO NOTHING no bloquea una fila ya commiteada, así que el publicador podía sacarla, leer el saldo sin
    // este cambio y dejar la cola vacía con el evento perdido. Bloqueada hasta el commit, SKIP LOCKED la deja para la siguiente vuelta;
    // el costo es que los escritores de una misma clave se serializan desde aquí hasta confirmar.
    public Task EnqueueAsync(IReadOnlyCollection<BalanceChangeKey> keys, CancellationToken ct)
    {
        var ordered = keys
            .Distinct()
            .OrderBy(key => key.TenantId)
            .ThenBy(key => key.OwnerId)
            .ThenBy(key => key.WarehouseId)
            .ThenBy(key => key.VariantId)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Task.CompletedTask;
        }

        var rows = string.Join(", ", ordered.Select((_, index) => Placeholders(index * ColumnsPerKey)));
        var values = ordered.SelectMany(key => new object[] { key.TenantId, key.OwnerId, key.WarehouseId, key.VariantId }).ToArray();

        // Sólo los marcadores {n} se arman a mano; los valores viajan como parámetros.
        var sql = "INSERT INTO wms.balance_change_queue (tenant_id, owner_id, warehouse_id, variant_id) VALUES " + rows
            + " ON CONFLICT (tenant_id, owner_id, warehouse_id, variant_id) DO UPDATE SET queued_at = EXCLUDED.queued_at";
        return dbContext.Database.ExecuteSqlRawAsync(sql, values, ct);
    }

    public async Task<IReadOnlyList<BalanceChangeKey>> DequeueAllAsync(int max, CancellationToken ct) =>
        await dbContext.Database.SqlQuery<BalanceChangeKey>(
            $"""
            WITH taken AS (
                SELECT tenant_id, owner_id, warehouse_id, variant_id
                FROM wms.balance_change_queue
                ORDER BY queued_at
                LIMIT {max}
                FOR UPDATE SKIP LOCKED)
            DELETE FROM wms.balance_change_queue queue
            USING taken
            WHERE queue.tenant_id = taken.tenant_id
              AND queue.owner_id = taken.owner_id
              AND queue.warehouse_id = taken.warehouse_id
              AND queue.variant_id = taken.variant_id
            RETURNING queue.tenant_id AS "TenantId", queue.owner_id AS "OwnerId",
                      queue.warehouse_id AS "WarehouseId", queue.variant_id AS "VariantId"
            """)
            .ToListAsync(ct);

    private static string Placeholders(int first) =>
        string.Create(CultureInfo.InvariantCulture, $"({{{first}}}, {{{first + 1}}}, {{{first + 2}}}, {{{first + 3}}})");
}

using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class PartitionMaintenance(WmsDbContext dbContext) : IPartitionMaintenance
{
    // El literal sale de un DateOnly formateado, no de texto del llamador.
    public Task EnsureAsync(DateOnly month, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync(InventoryLedgerSql.EnsurePartition(month), cancellationToken);

    // Adjunta de verdad y no sólo "existe una tabla con ese nombre": una desprendida no recibe filas del mes.
    public async Task<bool> ExistsAsync(DateOnly month, CancellationToken cancellationToken)
    {
        var name = $"stock_movements_y{month:yyyy}m{month:MM}";
        var found = await dbContext.Database.SqlQuery<bool>(
            $"""
            SELECT EXISTS (
                SELECT 1 FROM pg_inherits i
                JOIN pg_class child ON child.oid = i.inhrelid
                JOIN pg_class parent ON parent.oid = i.inhparent
                JOIN pg_namespace n ON n.oid = parent.relnamespace
                WHERE n.nspname = 'wms' AND parent.relname = 'stock_movements' AND child.relname = {name}) AS "Value"
            """)
            .ToListAsync(cancellationToken);
        return found.Single();
    }
}

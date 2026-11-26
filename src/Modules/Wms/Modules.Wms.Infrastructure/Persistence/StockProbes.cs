using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Persistence;

// Las tres sondas leen el libro de saldos (wms.stock_balances). «Con existencias» es saldo físico: on_hand o reserved distinto de cero
// en una ubicación que NO es virtual. La virtualidad sale de locations (S1): stock_balances ya no la guarda. Un saldo en SUPPLIER,
// CUSTOMER o ADJUSTMENT es contrapartida contable, no mercancía en bodega, y no puede bloquear un cambio de perfil ni un borrado.

internal sealed class VariantStockProbe(WmsDbContext dbContext) : IVariantStockProbe
{
    public async Task<bool> HasStockAsync(Guid tenantId, Guid variantId, CancellationToken ct) =>
        (await dbContext.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM wms.stock_balances b
                JOIN wms.locations l ON l.id = b.location_id
                WHERE b.tenant_id = {tenantId} AND b.variant_id = {variantId}
                  AND NOT l.is_virtual AND (b.on_hand <> 0 OR b.reserved <> 0)) AS "Value"
            """).ToListAsync(ct)).Single();

    // Una sola ida para hasta 200 variantes (GET /variant-profiles): el mismo filtro que HasStockAsync.
    public async Task<IReadOnlySet<Guid>> HasStockForManyAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(variantIds);
        if (variantIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = variantIds.Distinct().ToArray();
        var withStock = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT b.variant_id AS "Value"
            FROM wms.stock_balances b
            JOIN wms.locations l ON l.id = b.location_id
            WHERE b.tenant_id = {tenantId} AND b.variant_id = ANY({ids})
              AND NOT l.is_virtual AND (b.on_hand <> 0 OR b.reserved <> 0)
            """).ToListAsync(ct);
        return withStock.ToHashSet();
    }
}

internal sealed class LocationStockProbe(WmsDbContext dbContext) : ILocationStockProbe
{
    public async Task<bool> HasStockAsync(Guid tenantId, Guid locationId, CancellationToken ct) =>
        (await dbContext.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM wms.stock_balances b
                JOIN wms.locations l ON l.id = b.location_id
                WHERE b.tenant_id = {tenantId} AND b.location_id = {locationId}
                  AND NOT l.is_virtual AND (b.on_hand <> 0 OR b.reserved <> 0)) AS "Value"
            """).ToListAsync(ct)).Single();
}

internal sealed class OwnerStockProbe(WmsDbContext dbContext) : IOwnerStockProbe
{
    public async Task<bool> HasStockAsync(Guid tenantId, Guid ownerId, CancellationToken ct) =>
        (await dbContext.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM wms.stock_balances b
                JOIN wms.locations l ON l.id = b.location_id
                WHERE b.tenant_id = {tenantId} AND b.owner_id = {ownerId}
                  AND NOT l.is_virtual AND (b.on_hand <> 0 OR b.reserved <> 0)) AS "Value"
            """).ToListAsync(ct)).Single();
}

using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class WarehouseRepository(WmsDbContext dbContext) : IWarehouseRepository
{
    public void Add(Warehouse warehouse) => dbContext.Warehouses.Add(warehouse);

    // Rastreada y con el árbol entero: las reglas del agregado (código único entre hermanas, ciclos, reubicación) necesitan ver
    // todas las ubicaciones, y una bodega son pocas filas (§11.2).
    public Task<Warehouse?> GetAsync(Guid tenantId, WarehouseId id, CancellationToken ct) =>
        dbContext.Warehouses
            .Include(warehouse => warehouse.Locations)
            .FirstOrDefaultAsync(warehouse => warehouse.TenantId == tenantId && warehouse.Id == id, ct);

    public async Task<IReadOnlyList<Warehouse>> ListAsync(Guid tenantId, bool includeInactive, CancellationToken ct) =>
        await dbContext.Warehouses
            .AsNoTracking()
            .Include(warehouse => warehouse.Locations)
            .Where(warehouse => warehouse.TenantId == tenantId && (includeInactive || warehouse.IsActive))
            .OrderBy(warehouse => warehouse.Code)
            .ToListAsync(ct);

    public Task<Location?> GetLocationAsync(Guid tenantId, LocationId id, CancellationToken ct) =>
        dbContext.Locations.FirstOrDefaultAsync(location => location.TenantId == tenantId && location.Id == id, ct);

    public Task<Location?> GetVirtualLocationAsync(Guid tenantId, WarehouseId warehouseId, LocationRole role, CancellationToken ct) =>
        dbContext.Locations.FirstOrDefaultAsync(
            location => location.TenantId == tenantId
                && location.WarehouseId == warehouseId
                && location.IsVirtual
                && location.Role == role,
            ct);

    public void RemoveLocation(Location location) => dbContext.Locations.Remove(location);

    public async Task<Location?> FindLocationByCodeAsync(Guid tenantId, WarehouseId warehouseId, string code, CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = LocationCodes.Normalize(code, WmsErrorCodes.LocationCodeInvalid);
        }
        catch (WmsDomainException)
        {
            return null;
        }

        return await dbContext.Locations.AsNoTracking().FirstOrDefaultAsync(
            location => location.TenantId == tenantId && location.WarehouseId == warehouseId && location.Code == normalized, ct);
    }
}

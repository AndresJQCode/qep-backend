using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class UnitOfMeasureRepository(WmsDbContext dbContext) : IUnitOfMeasureRepository
{
    public void Add(UnitOfMeasure uom) => dbContext.UnitsOfMeasure.Add(uom);

    public Task<UnitOfMeasure?> FindAsync(Guid tenantId, UomCode code, CancellationToken ct) =>
        dbContext.UnitsOfMeasure.FirstOrDefaultAsync(uom => uom.TenantId == tenantId && uom.Code == code, ct);

    public async Task<IReadOnlyList<UnitOfMeasure>> ListAsync(Guid tenantId, CancellationToken ct) =>
        await dbContext.UnitsOfMeasure
            .AsNoTracking()
            .Where(uom => uom.TenantId == tenantId)
            .OrderBy(uom => uom.IsSystem ? 0 : 1)
            .ThenBy(uom => uom.Code)
            .ToListAsync(ct);
}

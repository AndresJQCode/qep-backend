using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class AdjustmentRepository(WmsDbContext dbContext) : IAdjustmentRepository
{
    public Task<Adjustment?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<Adjustment>()
            .Include(adjustment => adjustment.Lines)
            .FirstOrDefaultAsync(adjustment => adjustment.TenantId == tenantId && adjustment.Id == id, cancellationToken);

    public void Add(Adjustment adjustment) => dbContext.Set<Adjustment>().Add(adjustment);

    public async Task<AdjustmentPage> ListAsync(
        Guid tenantId, AdjustmentStatus? status, Guid? warehouseId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Set<Adjustment>().AsNoTracking().Where(adjustment => adjustment.TenantId == tenantId);
        if (status is { } wanted)
        {
            query = query.Where(adjustment => adjustment.Status == wanted);
        }

        if (warehouseId is { } warehouse)
        {
            var id = new WarehouseId(warehouse);
            query = query.Where(adjustment => adjustment.WarehouseId == id);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Include(adjustment => adjustment.Lines)
            .OrderByDescending(adjustment => adjustment.ProposedAt)
            .ThenByDescending(adjustment => adjustment.Number)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new AdjustmentPage(items, total);
    }

    // S26: el lock bloqueante va antes del conteo y vive hasta que la transacción del Propose confirme: dos propuestas simultáneas
    // numeran en serie, la segunda ve a la primera y toma el siguiente. Un try-lock devolvería 409 al segundo cliente por algo que no
    // es un conflicto. El índice único del número queda como último recurso.
    public async Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("NextNumberAsync requires the transaction from BeginTransactionAsync.");
        }

        var lockKey = "adj-seq:" + tenantId.ToString("D");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({WmsUnitOfWork.WmsLockNamespace}, hashtext({lockKey}))", cancellationToken);

        var prefix = "ADJ-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        var pattern = prefix + "%";
        var existing = await dbContext.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM wms.adjustments WHERE tenant_id = {tenantId} AND number LIKE {pattern}")
            .ToListAsync(cancellationToken);
        return prefix + (existing.Single() + 1).ToString("D4", CultureInfo.InvariantCulture);
    }
}

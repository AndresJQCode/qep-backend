using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class IncidentRepository(WmsDbContext dbContext) : IIncidentRepository
{
    public void Add(Incident incident) => dbContext.Set<Incident>().Add(incident);

    public Task<Incident?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<Incident>()
            .AsNoTracking()
            .AsSplitQuery()
            .Include(incident => incident.Evidence)
            .Include(incident => incident.Events)
            .FirstOrDefaultAsync(incident => incident.TenantId == tenantId && incident.Id == id, cancellationToken);

    public Task<bool> ExistsWithOperationKeyAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken) =>
        dbContext.Set<Incident>()
            .AsNoTracking()
            .AnyAsync(incident => incident.TenantId == tenantId && incident.OperationKey == operationKey, cancellationToken);

    // Como la numeración de ajustes y de tareas: el lock bloqueante va antes del conteo y vive hasta que la transacción confirme, así
    // dos reportes simultáneos numeran en serie. El índice único del número queda como último recurso.
    public async Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("NextNumberAsync requires the transaction from BeginTransactionAsync.");
        }

        var lockKey = "incident-seq:" + tenantId.ToString("D");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({WmsUnitOfWork.WmsLockNamespace}, hashtext({lockKey}))", cancellationToken);

        var prefix = "NOV-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        var pattern = prefix + "%";
        var existing = await dbContext.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM wms.incidents WHERE tenant_id = {tenantId} AND number LIKE {pattern}")
            .ToListAsync(cancellationToken);
        return prefix + (existing.Single() + 1).ToString("D4", CultureInfo.InvariantCulture);
    }
}

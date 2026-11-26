using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class WarehouseTaskRepository(WmsDbContext dbContext) : IWarehouseTaskRepository
{
    public void Add(WarehouseTask task) => dbContext.Set<WarehouseTask>().Add(task);

    public Task<WarehouseTask?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        WithLines(dbContext.Set<WarehouseTask>())
            .FirstOrDefaultAsync(task => task.TenantId == tenantId && task.Id == id, cancellationToken);

    // Sólo la fila de la tarea: es lo que serializa dos confirmaciones y a quien la reclama. Sin composición de EF encima, que
    // envolvería el FOR UPDATE en una subconsulta.
    public async Task<bool> LockAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        EnsureTransaction(nameof(LockAsync));
        var locked = await dbContext.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM wms.tasks WHERE tenant_id = {tenantId} AND id = {id} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return locked.Count == 1;
    }

    // Dos colecciones: en consultas separadas, para no multiplicar líneas por eventos.
    public Task<WarehouseTask?> GetWithEventsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        WithLines(dbContext.Set<WarehouseTask>().AsNoTracking())
            .Include(task => task.Events)
            .AsSplitQuery()
            .FirstOrDefaultAsync(task => task.TenantId == tenantId && task.Id == id, cancellationToken);

    public async Task<WarehouseTaskPage> ListAsync(
        Guid tenantId,
        Guid? warehouseId,
        WarehouseTaskStatus? status,
        WarehouseTaskType? type,
        Guid? assignedMemberId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = OfTenant(tenantId);
        if (warehouseId is { } warehouse)
        {
            var id = new WarehouseId(warehouse);
            query = query.Where(task => task.WarehouseId == id);
        }

        if (status is { } wantedStatus)
        {
            query = query.Where(task => task.Status == wantedStatus);
        }

        if (type is { } wantedType)
        {
            query = query.Where(task => task.Type == wantedType);
        }

        if (assignedMemberId is { } member)
        {
            query = query.Where(task => task.AssignedMemberId == member);
        }

        var total = await query.CountAsync(cancellationToken);
        var size = WmsPaging.NormalizePageSize(pageSize);
        var items = await WithLines(query)
            .OrderByDescending(task => task.CreatedAt)
            .ThenByDescending(task => task.Number)
            .Skip((WmsPaging.NormalizePage(page) - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        return new WarehouseTaskPage(items, total);
    }

    public async Task<IReadOnlyList<TaskStatusCount>> CountByStatusAsync(
        Guid tenantId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var id = new WarehouseId(warehouseId);
        var counts = await OfTenant(tenantId)
            .Where(task => task.WarehouseId == id)
            .GroupBy(task => task.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return [.. counts.Select(value => new TaskStatusCount(value.Status, value.Count))];
    }

    public async Task<IReadOnlyList<WarehouseTask>> ListInProgressAsync(
        Guid tenantId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var id = new WarehouseId(warehouseId);
        return await WithLines(OfTenant(tenantId))
            .Where(task => task.WarehouseId == id && task.Status == WarehouseTaskStatus.InProgress)
            .OrderBy(task => task.Number)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WarehouseTask>> ListAssignedToAsync(
        Guid tenantId, Guid warehouseId, Guid memberId, CancellationToken cancellationToken)
    {
        var id = new WarehouseId(warehouseId);
        // PostgreSQL ordena los nulos al final en ascendente: lo que tiene corte va primero.
        return await WithLines(OfTenant(tenantId))
            .Where(task => task.WarehouseId == id
                && task.Status == WarehouseTaskStatus.Assigned
                && task.AssignedMemberId == memberId)
            .OrderBy(task => task.DueAt)
            .ThenBy(task => task.ReleasedAt)
            .ThenBy(task => task.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<WarehouseTask?> FindInProgressOfMemberAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
        WithLines(OfTenant(tenantId))
            .FirstOrDefaultAsync(
                task => task.ClaimedByMemberId == memberId && task.Status == WarehouseTaskStatus.InProgress, cancellationToken);

    // Mismo mecanismo que la numeración de ajustes (S26): lock bloqueante antes del conteo, vivo hasta que la transacción confirme.
    public async Task<string> NextNumberAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        EnsureTransaction(nameof(NextNumberAsync));

        var lockKey = "task-seq:" + tenantId.ToString("D");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({WmsUnitOfWork.WmsLockNamespace}, hashtext({lockKey}))", cancellationToken);

        var prefix = "TSK-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        var pattern = prefix + "%";
        var existing = await dbContext.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM wms.tasks WHERE tenant_id = {tenantId} AND number LIKE {pattern}")
            .ToListAsync(cancellationToken);
        return prefix + (existing.Single() + 1).ToString("D4", CultureInfo.InvariantCulture);
    }

    private IQueryable<WarehouseTask> OfTenant(Guid tenantId) =>
        dbContext.Set<WarehouseTask>().AsNoTracking().Where(task => task.TenantId == tenantId);

    private static IQueryable<WarehouseTask> WithLines(IQueryable<WarehouseTask> query) =>
        query.Include(task => task.Lines.OrderBy(line => line.Position));

    private void EnsureTransaction(string operation)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException($"{operation} requires the transaction from BeginTransactionAsync.");
        }
    }
}

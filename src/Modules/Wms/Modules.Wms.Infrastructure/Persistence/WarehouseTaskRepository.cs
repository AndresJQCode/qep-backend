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

    public async Task<IReadOnlyList<TaskTypeCount>> CountOpenByTypeAsync(
        Guid tenantId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var id = new WarehouseId(warehouseId);
        var counts = await OfTenant(tenantId)
            .Where(task => task.WarehouseId == id
                && task.Status != WarehouseTaskStatus.Done
                && task.Status != WarehouseTaskStatus.Cancelled)
            .GroupBy(task => task.Type)
            .Select(group => new { Type = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return [.. counts.Select(value => new TaskTypeCount(value.Type, value.Count))];
    }

    public async Task<IReadOnlyDictionary<Guid, string>> FindNumbersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = taskIds.ToArray();
        var rows = await dbContext.Set<WarehouseTask>()
            .AsNoTracking()
            .Where(task => task.TenantId == tenantId && ids.Contains(task.Id))
            .Select(task => new { task.Id, task.Number })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Id, row => row.Number);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> FindLineDestinationsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        if (lineIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        var ids = lineIds.ToArray();
        var rows = await dbContext.Set<WarehouseTask>()
            .AsNoTracking()
            .Where(task => task.TenantId == tenantId)
            .SelectMany(task => task.Lines)
            .Where(line => ids.Contains(line.Id) && line.ToLocationId != null)
            .Select(line => new { line.Id, line.ToLocationId })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Id, row => row.ToLocationId!.Value.Value);
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> FindClaimedAtAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return new Dictionary<Guid, DateTimeOffset>();
        }

        var ids = taskIds.ToArray();
        var claims = await dbContext.Set<WarehouseTaskEvent>()
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId && ids.Contains(value.TaskId) && value.Kind == TaskEventKind.Claimed)
            .GroupBy(value => value.TaskId)
            .Select(group => new { TaskId = group.Key, At = group.Max(value => value.OccurredAt) })
            .ToListAsync(cancellationToken);
        return claims.ToDictionary(value => value.TaskId, value => value.At);
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

    public Task<bool> HasMemberClaimedAsync(Guid tenantId, Guid taskId, Guid memberId, CancellationToken cancellationToken) =>
        dbContext.Set<WarehouseTaskEvent>()
            .AsNoTracking()
            .AnyAsync(
                value => value.TenantId == tenantId && value.TaskId == taskId && value.MemberId == memberId
                    && value.Kind == TaskEventKind.Claimed,
                cancellationToken);

    // Los pesos (10 de urgente, un punto por cada 1200 s en el pool con tope 3, y los tramos del corte) repiten TaskPriority:
    // TaskCandidateQueryTests amarra las dos versiones y cambiar una sin la otra lo pone en rojo. SKIP LOCKED hace que dos operarios
    // que piden a la vez reciban tareas distintas sin esperarse. Como en LockAsync, va sin composición de EF encima.
    public async Task<Guid?> LockNextCandidateAsync(
        Guid tenantId,
        Guid warehouseId,
        Guid memberId,
        IReadOnlyCollection<WarehouseTaskType> types,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureTransaction(nameof(LockNextCandidateAsync));
        var typeNames = types.Select(type => type.ToString()).ToArray();
        var skippedSince = now - TaskTypePolicy.SkipCooldown;

        // DA-33: lo que la persona saltó hace poco no se le vuelve a ofrecer, salvo que se lo hayan asignado. El FOR UPDATE sólo
        // bloquea la fila de la tarea: la subconsulta de la historia no bloquea nada.
        var candidates = await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                SELECT t.id AS "Value" FROM wms.tasks t
                WHERE t.tenant_id = {tenantId} AND t.warehouse_id = {warehouseId}
                  AND t.status IN ('Ready','Assigned')
                  AND (t.assigned_member_id IS NULL OR t.assigned_member_id = {memberId})
                  AND t.type = ANY({typeNames})
                  AND (t.assigned_member_id = {memberId} OR NOT EXISTS (
                        SELECT 1 FROM wms.task_events e
                        WHERE e.task_id = t.id AND e.kind = 'Unclaimed' AND e.detail = 'Skipped'
                          AND e.member_id = {memberId} AND e.occurred_at > {skippedSince}))
                ORDER BY (t.assigned_member_id IS NOT NULL) DESC,
                  (t.priority
                   + CASE WHEN t.urgent THEN 10 ELSE 0 END
                   + LEAST(3, GREATEST(0, FLOOR(EXTRACT(EPOCH FROM ({now} - t.released_at)) / 1200)))::int
                   + CASE WHEN t.due_at IS NULL THEN 0
                          WHEN t.due_at <= {now} THEN 4
                          WHEN t.due_at <= {now} + interval '30 minutes' THEN 3
                          WHEN t.due_at <= {now} + interval '1 hour' THEN 2
                          WHEN t.due_at <= {now} + interval '2 hours' THEN 1
                          ELSE 0 END) DESC,
                  t.due_at NULLS LAST, t.released_at, t.id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        return candidates.Count == 0 ? null : candidates[0];
    }

    // El mismo corte que WarehouseTask.ExpireLease (vencido es estrictamente anterior). SKIP LOCKED reparte las filas entre réplicas
    // y no espera a la confirmación que tenga una tomada: la ve en la pasada siguiente.
    public async Task<IReadOnlyList<ExpiredTaskLease>> LockExpiredAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        EnsureTransaction(nameof(LockExpiredAsync));

        return await dbContext.Database
            .SqlQuery<ExpiredTaskLease>(
                $"""
                SELECT t.tenant_id AS "TenantId", t.id AS "TaskId" FROM wms.tasks t
                WHERE t.status = 'InProgress' AND t.lease_expires_at < {now}
                ORDER BY t.lease_expires_at
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
    }

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

    public async Task<IReadOnlyList<AssignedTaskRef>> ListAssignedOpenAsync(
        Guid tenantId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var id = new WarehouseId(warehouseId);
        return await OfTenant(tenantId)
            .Where(task => task.WarehouseId == id
                && (task.Status == WarehouseTaskStatus.Planned || task.Status == WarehouseTaskStatus.Assigned)
                && task.AssignedMemberId != null)
            .OrderBy(task => task.Number)
            .Select(task => new AssignedTaskRef(task.Id, task.Number, task.Type, task.AssignedMemberId!.Value))
            .ToListAsync(cancellationToken);
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

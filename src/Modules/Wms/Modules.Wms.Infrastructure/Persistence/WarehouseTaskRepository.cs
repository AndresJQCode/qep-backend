using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
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
        var counts = await OfTenantKnown(tenantId)
            .Where(task => task.WarehouseId == id)
            .GroupBy(task => task.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return [.. counts.Select(value => new TaskStatusCount(value.Status, value.Count))];
    }

    // «Abierta» es todo estado que no sea Done ni Cancelled: T2 (el tipo Count) no agrega ningún estado de tarea, así que esta lista y
    // la de CatalogVariantUsageChecker no cambian. Un estado nuevo en el futuro obliga a revisar las dos.
    public async Task<IReadOnlyList<TaskTypeCount>> CountOpenByTypeAsync(
        Guid tenantId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var id = new WarehouseId(warehouseId);
        var counts = await OfTenantKnown(tenantId)
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
        // Con líneas: una tarea con un estado de línea que este build no conoce tampoco se puede leer (ver KnownTaskValues).
        return await WithLines(OfTenantKnown(tenantId))
            .Where(task => task.WarehouseId == id
                && task.Status == WarehouseTaskStatus.InProgress
                && !task.Lines.Any(line => !KnownTaskValues.LineStatuses.Contains(line.Status)))
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

    // Sólo cuenta el Count con una línea abierta sobre exactamente esa clave. Lote nulo es «sin lote» (IS NOT DISTINCT FROM; el cast
    // a uuid tipa el parámetro nulo). Sin FOR UPDATE: quien enlaza bloquea la fila después y vuelve a mirar su estado.
    public async Task<Guid?> FindOpenCountAsync(
        Guid tenantId,
        Guid warehouseId,
        Guid ownerId,
        Guid locationId,
        Guid variantId,
        Guid? lotId,
        StockStatus stockStatus,
        CancellationToken cancellationToken)
    {
        var status = stockStatus.ToString();
        var found = await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                SELECT t.id AS "Value" FROM wms.tasks t
                JOIN wms.task_lines l ON l.task_id = t.id
                WHERE t.tenant_id = {tenantId} AND t.warehouse_id = {warehouseId} AND t.owner_id = {ownerId}
                  AND t.type = 'Count' AND t.status NOT IN ('Done','Cancelled')
                  AND l.line_status = 'Open' AND l.from_location_id = {locationId} AND l.variant_id = {variantId}
                  AND l.lot_id IS NOT DISTINCT FROM {lotId}::uuid AND l.stock_status = {status}
                ORDER BY t.created_at, t.id
                LIMIT 1
                """)
            .ToListAsync(cancellationToken);
        return found.Count == 0 ? null : found[0];
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
        // DA-45: tampoco se le ofrece el Count que verifica una novedad que ella misma reportó (no cuenta su propio faltante), salvo
        // que el supervisor se lo haya asignado. Lee wms.incidents por nombre de columna (verification_task_id, reported_by): ver
        // la regla sql-entre-modulos. La subconsulta tampoco bloquea nada.
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
                  AND (t.assigned_member_id = {memberId} OR NOT EXISTS (
                        SELECT 1 FROM wms.incidents i
                        WHERE i.verification_task_id = t.id AND i.reported_by = {memberId}))
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

        // Sólo lo que este build sabe materializar (KnownTaskValues): un pod más nuevo puede escribir un tipo, un origen o un estado de
        // línea que éste no conoce, y una fila que lanza al cargarse deshace todo el lote en cada pasada. Esa fila se salta: la barre
        // el build que la entiende.
        var types = KnownTaskValues.TypeNames;
        var sources = KnownTaskValues.SourceNames;
        var lineStatuses = KnownTaskValues.LineStatusNames;
        return await dbContext.Database
            .SqlQuery<ExpiredTaskLease>(
                $"""
                SELECT t.tenant_id AS "TenantId", t.id AS "TaskId" FROM wms.tasks t
                WHERE t.status = 'InProgress' AND t.lease_expires_at < {now}
                  AND t.type = ANY({types}) AND t.source_type = ANY({sources})
                  AND NOT EXISTS (
                        SELECT 1 FROM wms.task_lines l WHERE l.task_id = t.id AND l.line_status <> ALL({lineStatuses}))
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
        return await OfTenantKnown(tenantId)
            .Where(task => task.WarehouseId == id
                && (task.Status == WarehouseTaskStatus.Planned || task.Status == WarehouseTaskStatus.Assigned)
                && task.AssignedMemberId != null)
            .OrderBy(task => task.Number)
            .Select(task => new AssignedTaskRef(task.Id, task.Number, task.Type, task.AssignedMemberId!.Value))
            .ToListAsync(cancellationToken);
    }

    // Para las lecturas del tablero: sólo las tareas con un tipo, un estado y un origen que este build sabe leer. Un pod más nuevo
    // puede escribir otros durante un despliegue, y una sola fila que no se pueda materializar rompería el tablero entero. Es
    // KnownTaskValues quien dice cuáles son; no quites el filtro.
    private IQueryable<WarehouseTask> OfTenantKnown(Guid tenantId) =>
        OfTenant(tenantId).Where(task => KnownTaskValues.Types.Contains(task.Type)
            && KnownTaskValues.Statuses.Contains(task.Status)
            && KnownTaskValues.Sources.Contains(task.SourceType));

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

using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record GetTaskBoardQuery(Guid TenantId, Guid WarehouseId) : IQuery<TaskBoardDto>;

public sealed class GetTaskBoardHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseRepository warehouses,
    IWarehouseTaskRepository tasks,
    IIncidentRepository incidents,
    IWmsMemberDirectory members,
    IClock clock)
    : IQueryHandler<GetTaskBoardQuery, TaskBoardDto>
{
    /// <summary>«Sin avance hace X minutos» (B4): el umbral es decisión del agente, por confirmar con el owner.</summary>
    public static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(15);

    public async Task<TaskBoardDto> HandleAsync(GetTaskBoardQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.DocumentRead, warehouseId: query.WarehouseId, ct: cancellationToken);
        _ = await warehouses.GetAsync(query.TenantId, new WarehouseId(query.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(query.WarehouseId);

        var byStatus = (await tasks.CountByStatusAsync(query.TenantId, query.WarehouseId, cancellationToken))
            .ToDictionary(count => count.Status, count => count.Count);
        var byType = (await tasks.CountOpenByTypeAsync(query.TenantId, query.WarehouseId, cancellationToken))
            .ToDictionary(count => count.Type, count => count.Count);

        var openIncidents = await incidents.CountOpenAsync(query.TenantId, query.WarehouseId, cancellationToken);

        // Una InProgress siempre tiene quien la reclamó (CK_tasks_claim).
        var working = await tasks.ListInProgressAsync(query.TenantId, query.WarehouseId, cancellationToken);
        var claimedAt = await tasks.FindClaimedAtAsync(
            query.TenantId, [.. working.Select(task => task.Id)], cancellationToken);
        var progressAt = await tasks.FindLastProgressAtAsync(
            query.TenantId, [.. working.Select(task => task.Id)], cancellationToken);
        var now = clock.UtcNow;

        // M1: una consulta de asignadas y una de elegibles por permiso (el de cada tipo distinto y wms.document.read, que exige toda
        // ruta de tareas; nunca una por tarea). Sin wms.document.read tampoco puede reclamar su asignada.
        var assigned = await tasks.ListAssignedOpenAsync(query.TenantId, query.WarehouseId, cancellationToken);
        var eligible = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        if (assigned.Count > 0)
        {
            foreach (var permission in assigned
                         .Select(task => TaskTypePermissions.For(task.Type))
                         .Append(WmsPermissions.DocumentRead)
                         .Distinct(StringComparer.Ordinal))
            {
                eligible[permission] = [.. await members.ListWithPermissionAsync(query.TenantId, permission, cancellationToken)];
            }
        }

        var stale = assigned
            .Where(task => !eligible[TaskTypePermissions.For(task.Type)].Contains(task.AssignedMemberId)
                || !eligible[WmsPermissions.DocumentRead].Contains(task.AssignedMemberId))
            .ToList();
        var names = await members.FindNamesAsync(
            query.TenantId,
            [.. working.Select(task => task.ClaimedByMemberId!.Value).Concat(stale.Select(task => task.AssignedMemberId)).Distinct()],
            cancellationToken);

        return new TaskBoardDto(
            query.WarehouseId,
            [
                .. Enum.GetValues<WarehouseTaskStatus>()
                    .Select(status => new TaskStatusSliceDto(status.ToString(), byStatus.GetValueOrDefault(status))),
            ],
            [
                .. Enum.GetValues<WarehouseTaskType>()
                    .Select(type => new TaskTypeSliceDto(type.ToString(), byType.GetValueOrDefault(type))),
            ],
            [
                .. working
                    .Select(task => new TaskOperatorDto(
                        task.ClaimedByMemberId!.Value,
                        names.GetValueOrDefault(task.ClaimedByMemberId.Value) ?? "Sin nombre",
                        task.Id,
                        task.Number,
                        task.Type.ToString(),
                        claimedAt.TryGetValue(task.Id, out var since) ? since : null,
                        task.LastActivityAt,
                        progressAt.TryGetValue(task.Id, out var progress) ? progress : null,
                        progressAt.TryGetValue(task.Id, out var last) && now - last >= StalledAfter))
                    .OrderBy(worker => worker.Name, StringComparer.InvariantCultureIgnoreCase)
                    .ThenBy(worker => worker.TaskNumber, StringComparer.Ordinal),
            ],
            [
                .. stale.Select(task => new TaskStaleAssignmentDto(
                    task.TaskId, task.Number, task.AssignedMemberId, names.GetValueOrDefault(task.AssignedMemberId) ?? "Sin nombre")),
            ],
            openIncidents);
    }
}

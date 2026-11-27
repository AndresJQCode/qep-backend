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
    IWmsMemberDirectory members)
    : IQueryHandler<GetTaskBoardQuery, TaskBoardDto>
{
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

        // Una InProgress siempre tiene quien la reclamó (CK_tasks_claim).
        var working = await tasks.ListInProgressAsync(query.TenantId, query.WarehouseId, cancellationToken);
        var claimedAt = await tasks.FindClaimedAtAsync(
            query.TenantId, [.. working.Select(task => task.Id)], cancellationToken);

        // M1: una consulta de asignadas y una de elegibles por permiso (tantas como tipos distintos, nunca una por tarea).
        var assigned = await tasks.ListAssignedOpenAsync(query.TenantId, query.WarehouseId, cancellationToken);
        var eligible = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var permission in assigned.Select(task => TaskTypePermissions.For(task.Type)).Distinct(StringComparer.Ordinal))
        {
            eligible[permission] = [.. await members.ListWithPermissionAsync(query.TenantId, permission, cancellationToken)];
        }

        var stale = assigned
            .Where(task => !eligible[TaskTypePermissions.For(task.Type)].Contains(task.AssignedMemberId))
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
                        task.LastActivityAt))
                    .OrderBy(worker => worker.Name, StringComparer.InvariantCultureIgnoreCase)
                    .ThenBy(worker => worker.TaskNumber, StringComparer.Ordinal),
            ],
            [
                .. stale.Select(task => new TaskStaleAssignmentDto(
                    task.TaskId, task.Number, task.AssignedMemberId, names.GetValueOrDefault(task.AssignedMemberId) ?? "Sin nombre")),
            ]);
    }
}

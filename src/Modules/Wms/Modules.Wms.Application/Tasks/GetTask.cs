using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record GetTaskQuery(Guid TenantId, Guid TaskId) : IQuery<TaskDto>;

/// <summary>La ficha de una tarea, con su historia.</summary>
public sealed class GetTaskHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWarehouseTaskRepository tasks,
    WarehouseTaskDtoComposer composer)
    : IQueryHandler<GetTaskQuery, TaskDto>
{
    public async Task<TaskDto> HandleAsync(GetTaskQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(executionContext, query.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);

        var task = await tasks.GetWithEventsAsync(query.TenantId, query.TaskId, cancellationToken)
            ?? throw WmsNotFound.Task(query.TaskId);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.DocumentRead, task.OwnerId.Value, task.WarehouseId.Value, cancellationToken);

        var viewer = await caller.FindAsync(query.TenantId, cancellationToken);
        return await composer.ComposeAsync(task, viewer, withEvents: true, cancellationToken);
    }
}

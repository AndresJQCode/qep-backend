using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record GetMyTasksQuery(Guid TenantId, Guid WarehouseId) : IQuery<MyTasksDto>;

/// <summary>«Mis tareas» (spec 2026-11-26 §6.5): la que tiene en curso en la bodega y las que le asignaron, sólo de los tipos que
/// puede hacer.</summary>
public sealed class GetMyTasksHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWarehouseTaskRepository tasks,
    WarehouseTaskDtoComposer composer)
    : IQueryHandler<GetMyTasksQuery, MyTasksDto>
{
    public async Task<MyTasksDto> HandleAsync(GetMyTasksQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.DocumentRead, warehouseId: query.WarehouseId, ct: cancellationToken);
        var types = TaskTypePermissions.AllowedFor(executionContext);
        if (types.Count == 0)
        {
            throw WmsAuthorization.Denied();
        }

        var member = await caller.RequireAsync(query.TenantId, cancellationToken);
        var current = await tasks.FindInProgressOfMemberAsync(query.TenantId, member, cancellationToken);
        var assigned = (await tasks.ListAssignedToAsync(query.TenantId, query.WarehouseId, member, cancellationToken))
            .Where(task => types.Contains(task.Type))
            .ToList();

        var shown = current is not null && current.WarehouseId.Value == query.WarehouseId && types.Contains(current.Type)
            ? await composer.ComposeAsync(current, member, withEvents: false, cancellationToken)
            : null;
        return new MyTasksDto(shown, await composer.ComposeSummariesAsync(query.TenantId, assigned, cancellationToken));
    }
}

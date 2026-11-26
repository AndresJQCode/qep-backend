using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>El motor de tareas (spec 2026-11-26 §6.11). El supervisor actúa con <c>wms.task.manage</c> y ve con
/// <c>wms.document.read</c>. Crear una tarea es IK (header <c>Idempotency-Key</c>).</summary>
internal static class TaskEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/tasks", ListAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<TaskPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/tasks", CreateAsync)
            .RequireIdempotencyKey()
            .RequireAuthorization(WmsPermissions.TaskManage)
            .Accepts<CreateMoveTaskRequest>("application/json")
            .Produces<TaskDto>(StatusCodes.Status201Created)
            .Produces<TaskDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/tasks/{id:guid}", GetAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<TaskDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        Guid? warehouseId,
        string? status,
        string? type,
        Guid? assignedMemberId,
        int? page,
        int? pageSize,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListTasksQuery(
                tenantId, warehouseId, status, type, assignedMemberId, page ?? 1, pageSize ?? WmsPaging.DefaultPageSize),
            cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateMoveTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var task = await dispatcher.SendAsync(CreateMoveTaskCommand.From(tenantId, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/wms/tasks/{task.Id}", task);
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetTaskQuery(tenantId, id), cancellationToken));
}

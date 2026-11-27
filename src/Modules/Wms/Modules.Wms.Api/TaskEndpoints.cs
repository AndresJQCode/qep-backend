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

        group.MapGet("/tasks/summary", BoardAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<TaskBoardDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/tasks/assignees", AssigneesAsync)
            .RequireAuthorization(WmsPermissions.TaskManage)
            .Produces<TaskAssigneesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        SupervisorAction<TaskVersionRequest>(group, "/tasks/{id:guid}/publish", PublishAsync);
        SupervisorAction<HoldTaskRequest>(group, "/tasks/{id:guid}/hold", HoldAsync);
        SupervisorAction<AssignTaskRequest>(group, "/tasks/{id:guid}/assign", AssignAsync);
        SupervisorAction<TaskVersionRequest>(group, "/tasks/{id:guid}/unassign", UnassignAsync);
        SupervisorAction<ChangeTaskPriorityRequest>(group, "/tasks/{id:guid}/priority", ChangePriorityAsync);
        SupervisorAction<CancelTaskRequest>(group, "/tasks/{id:guid}/cancel", CancelAsync);
        SupervisorAction<CancelTaskRequest>(group, "/tasks/{id:guid}/lines/{lineId:guid}/cancel", CancelLineAsync);

        MapOperator(group);
    }

    // El operario (spec §6.11): la ruta exige ver documentos y el handler, además, el permiso del tipo de la tarea.
    private static void MapOperator(RouteGroupBuilder group)
    {
        group.MapGet("/tasks/mine", MineAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<MyTasksDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/tasks/next", NextAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Accepts<NextTaskRequest>("application/json")
            .Produces<TaskDto>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        OperatorAction<ClaimTaskRequest, TaskDto>(group, "/tasks/{id:guid}/claim", ClaimAsync);
        OperatorAction<HeartbeatTaskRequest, TaskLeaseDto>(group, "/tasks/{id:guid}/heartbeat", HeartbeatAsync);
        OperatorAction<UnclaimTaskRequest, TaskDto>(group, "/tasks/{id:guid}/unclaim", UnclaimAsync);
        OperatorAction<ConfirmTaskLineRequest, TaskDto>(group, "/tasks/{id:guid}/lines/{lineId:guid}/confirm", ConfirmAsync)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
        OperatorAction<ShortTaskLineRequest, TaskDto>(group, "/tasks/{id:guid}/lines/{lineId:guid}/short", ShortAsync)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
        // El conteo ciego de una línea de Count (DA-40): la ruta exige ver documentos y el handler, el permiso del tipo (contar).
        OperatorAction<CountTaskLineRequest, TaskDto>(group, "/tasks/{id:guid}/lines/{lineId:guid}/count", CountAsync)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static RouteHandlerBuilder OperatorAction<TRequest, TResponse>(RouteGroupBuilder group, string route, Delegate handler)
        where TRequest : notnull =>
        group.MapPost(route, handler)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Accepts<TRequest>("application/json")
            .Produces<TResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    private static async Task<IResult> MineAsync(
        Guid tenantId, Guid warehouseId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetMyTasksQuery(tenantId, warehouseId), cancellationToken));

    private static async Task<IResult> NextAsync(
        Guid tenantId, NextTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(NextTaskCommand.From(tenantId, request), cancellationToken);
        return result.Task is null ? Results.NoContent() : Results.Ok(result.Task);
    }

    private static async Task<IResult> ClaimAsync(
        Guid tenantId, Guid id, ClaimTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(ClaimTaskCommand.From(tenantId, id, request), cancellationToken));

    private static async Task<IResult> HeartbeatAsync(
        Guid tenantId, Guid id, HeartbeatTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new HeartbeatTaskCommand(tenantId, id, request.LeaseToken), cancellationToken));

    private static async Task<IResult> UnclaimAsync(
        Guid tenantId, Guid id, UnclaimTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UnclaimTaskCommand(tenantId, id, request.LeaseToken, request.Reason), cancellationToken));

    private static async Task<IResult> ConfirmAsync(
        Guid tenantId,
        Guid id,
        Guid lineId,
        ConfirmTaskLineRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            ConfirmTaskLineCommand.From(tenantId, id, lineId, request), cancellationToken));

    private static async Task<IResult> ShortAsync(
        Guid tenantId,
        Guid id,
        Guid lineId,
        ShortTaskLineRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            ShortTaskLineCommand.From(tenantId, id, lineId, request), cancellationToken));

    private static async Task<IResult> CountAsync(
        Guid tenantId,
        Guid id,
        Guid lineId,
        CountTaskLineRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            CountTaskLineCommand.From(tenantId, id, lineId, request), cancellationToken));

    // Las siete comparten política, respuesta y errores: sólo cambian el cuerpo y el comando.
    private static void SupervisorAction<TRequest>(RouteGroupBuilder group, string route, Delegate handler)
        where TRequest : notnull =>
        group.MapPost(route, handler)
            .RequireAuthorization(WmsPermissions.TaskManage)
            .Accepts<TRequest>("application/json")
            .Produces<TaskDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    private static async Task<IResult> BoardAsync(
        Guid tenantId, Guid warehouseId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetTaskBoardQuery(tenantId, warehouseId), cancellationToken));

    private static async Task<IResult> AssigneesAsync(
        Guid tenantId, Guid warehouseId, string? type, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTaskAssigneesQuery(tenantId, warehouseId, type), cancellationToken));

    private static async Task<IResult> PublishAsync(
        Guid tenantId, Guid id, TaskVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new PublishTaskCommand(tenantId, id, request.Version), cancellationToken));

    private static async Task<IResult> HoldAsync(
        Guid tenantId, Guid id, HoldTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new HoldTaskCommand(tenantId, id, request.Version, request.Reason), cancellationToken));

    private static async Task<IResult> AssignAsync(
        Guid tenantId, Guid id, AssignTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new AssignTaskCommand(tenantId, id, request.MemberId, request.Version, request.Reason), cancellationToken));

    private static async Task<IResult> UnassignAsync(
        Guid tenantId, Guid id, TaskVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new UnassignTaskCommand(tenantId, id, request.Version), cancellationToken));

    private static async Task<IResult> ChangePriorityAsync(
        Guid tenantId,
        Guid id,
        ChangeTaskPriorityRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ChangeTaskPriorityCommand(tenantId, id, request.Priority, request.Urgent, request.Version), cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid tenantId, Guid id, CancelTaskRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(CancelTaskCommand.From(tenantId, id, request), cancellationToken));

    private static async Task<IResult> CancelLineAsync(
        Guid tenantId,
        Guid id,
        Guid lineId,
        CancelTaskRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            CancelTaskLineCommand.From(tenantId, id, lineId, request), cancellationToken));

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

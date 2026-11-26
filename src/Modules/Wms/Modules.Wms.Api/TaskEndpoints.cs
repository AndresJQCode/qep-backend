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
    }

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

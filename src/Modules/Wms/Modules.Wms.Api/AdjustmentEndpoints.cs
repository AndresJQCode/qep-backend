using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Ajustes de inventario (spec WMS 2026-10-31 §24.3). <c>POST /adjustments</c> es IK (header <c>Idempotency-Key</c>) y
/// <c>post</c> es SK (clave derivada de la versión, el header se ignora); <c>approve</c> y <c>reject</c> se protegen con la versión.</summary>
internal static class AdjustmentEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/adjustments", ListAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<AdjustmentPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/adjustments", ProposeAsync)
            .RequireIdempotencyKey()
            .RequireAuthorization(WmsPermissions.AdjustmentPropose)
            .Accepts<ProposeAdjustmentRequest>("application/json")
            .Produces<AdjustmentDto>(StatusCodes.Status201Created)
            .Produces<AdjustmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/adjustments/{id:guid}/approve", ApproveAsync)
            .RequireAuthorization(WmsPermissions.AdjustmentApprove)
            .Accepts<AdjustmentVersionRequest>("application/json")
            .Produces<AdjustmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/adjustments/{id:guid}/post", PostAsync)
            .UseServerIdempotencyKey("adjustment", "post")
            .RequireAuthorization(WmsPermissions.AdjustmentApprove)
            .Accepts<AdjustmentVersionRequest>("application/json")
            .Produces<AdjustmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/adjustments/{id:guid}/reject", RejectAsync)
            .RequireAuthorization(WmsPermissions.AdjustmentApprove)
            .Accepts<RejectAdjustmentRequest>("application/json")
            .Produces<AdjustmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId, string? status, Guid? warehouseId, int? page, int? pageSize, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListAdjustmentsQuery(tenantId, status, warehouseId, page ?? 1, pageSize ?? WmsPaging.DefaultPageSize), cancellationToken));

    private static async Task<IResult> ProposeAsync(
        Guid tenantId, ProposeAdjustmentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var adjustment = await dispatcher.SendAsync(ProposeAdjustmentCommand.From(tenantId, request), cancellationToken);

        // Los ajustes no tienen GET por id: el Location apunta al listado, que es donde se ve.
        return Results.Created($"/api/v1/tenants/{tenantId}/wms/adjustments", adjustment);
    }

    private static async Task<IResult> ApproveAsync(
        Guid tenantId, Guid id, AdjustmentVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ApproveAdjustmentCommand(tenantId, id, request.Version), cancellationToken));

    private static async Task<IResult> PostAsync(
        Guid tenantId, Guid id, AdjustmentVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new PostAdjustmentCommand(tenantId, id, request.Version), cancellationToken));

    private static async Task<IResult> RejectAsync(
        Guid tenantId, Guid id, RejectAdjustmentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(RejectAdjustmentCommand.From(tenantId, id, request), cancellationToken));
}

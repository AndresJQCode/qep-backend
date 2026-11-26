using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Saldos, resumen e historia de movimientos (spec WMS 2026-10-31 §24.3). Cuelga del grupo <c>/wms</c> de
/// <see cref="WmsEndpoints"/>. Las tres lecturas no provisionan ni escriben; el reverso de un movimiento es la única escritura.</summary>
internal static class StockEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/stock", ListStockAsync)
            .RequireAuthorization(WmsPermissions.StockRead)
            .Produces<StockPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/stock/summary", GetSummaryAsync)
            .RequireAuthorization(WmsPermissions.StockRead)
            .Produces<StockSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/stock/movements", ListMovementsAsync)
            .RequireAuthorization(WmsPermissions.StockRead)
            .Produces<MovementPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Como la exportación de Suppliers: 202 y el archivo llega por correo (RF-030). Los filtros son los de GET /stock, en el cuerpo (§24.3); sin cuerpo no hay filtro.
        group.MapPost("/stock/export", ExportStockAsync)
            .RequireAuthorization(WmsPermissions.StockExport)
            .Accepts<ExportStockRequest>(isOptional: true, "application/json")
            .Produces<StockExportAcceptedDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // IK: un movimiento no tiene versión, así que la clave la trae el cliente en el header.
        group.MapPost("/stock/movements/{id:guid}/reverse", ReverseMovementAsync)
            .RequireIdempotencyKey()
            .RequireAuthorization(WmsPermissions.AdjustmentApprove)
            .Accepts<ReverseMovementRequest>("application/json")
            .Produces<MovementDto>(StatusCodes.Status201Created)
            .Produces<MovementDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> ReverseMovementAsync(
        Guid tenantId, Guid id, ReverseMovementRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var reversal = await dispatcher.SendAsync(ReverseMovementCommand.From(tenantId, id, request), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/wms/stock/movements", reversal);
    }

    private static async Task<IResult> ExportStockAsync(
        Guid tenantId, ExportStockRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Accepted(value: await dispatcher.SendAsync(ExportStockCommand.From(tenantId, request), cancellationToken));

    private static async Task<IResult> ListStockAsync(
        Guid tenantId,
        Guid? warehouseId,
        Guid? ownerId,
        Guid? variantId,
        Guid? locationId,
        Guid? lotId,
        string? status,
        bool? onlyAvailable,
        bool? includeVirtual,
        string? search,
        int? page,
        int? pageSize,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new GetStockQuery(
                tenantId,
                warehouseId,
                ownerId,
                variantId,
                locationId,
                lotId,
                status,
                onlyAvailable is true,
                includeVirtual is true,
                search,
                page ?? 1,
                pageSize ?? WmsPaging.DefaultPageSize),
            cancellationToken));

    private static async Task<IResult> GetSummaryAsync(
        Guid tenantId, Guid? warehouseId, Guid? ownerId, Guid? variantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetStockSummaryQuery(tenantId, warehouseId, ownerId, variantId), cancellationToken));

    private static async Task<IResult> ListMovementsAsync(
        Guid tenantId,
        Guid? variantId,
        Guid? locationId,
        Guid? lotId,
        string? serial,
        string? documentType,
        Guid? documentId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListStockMovementsQuery(
                tenantId,
                variantId,
                locationId,
                lotId,
                serial,
                documentType,
                documentId,
                from,
                to,
                cursor,
                limit ?? ListStockMovementsQuery.DefaultLimit),
            cancellationToken));
}

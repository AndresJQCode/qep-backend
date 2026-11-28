using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Las bodegas (spec WMS 2026-10-31 §24.3). Cuelga del grupo <c>/wms</c> de <see cref="WmsEndpoints"/>.</summary>
internal static class WarehouseEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/warehouses", ListAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<WarehousesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/warehouses", CreateAsync)
            .RequireAuthorization(WmsPermissions.WarehouseManage)
            .Accepts<CreateWarehouseRequest>("application/json")
            .Produces<WarehouseDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/warehouses/{warehouseId:guid}", UpdateAsync)
            .RequireAuthorization(WmsPermissions.WarehouseManage)
            .Accepts<UpdateWarehouseRequest>("application/json")
            .Produces<WarehouseDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Inventario básico §5.3: elegir la bodega donde las compras suman y las ventas descuentan.
        group.MapPut("/warehouses/{warehouseId:guid}/principal", SetPrincipalAsync)
            .RequireAuthorization(WmsPermissions.WarehouseManage)
            .Accepts<SetPrincipalWarehouseRequest>("application/json")
            .Produces<WarehouseDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        bool includeInactive = false) =>
        Results.Ok(await dispatcher.QueryAsync(new ListWarehousesQuery(tenantId, includeInactive), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateWarehouseRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var warehouse = await dispatcher.SendAsync(CreateWarehouseCommand.From(tenantId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/wms/warehouses/{warehouse.Id}", warehouse);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid warehouseId, UpdateWarehouseRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(UpdateWarehouseCommand.From(tenantId, warehouseId, request), cancellationToken));

    private static async Task<IResult> SetPrincipalAsync(
        Guid tenantId, Guid warehouseId, SetPrincipalWarehouseRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(SetPrincipalWarehouseCommand.From(tenantId, warehouseId, request), cancellationToken));
}

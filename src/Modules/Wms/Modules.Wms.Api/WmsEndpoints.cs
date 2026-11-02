using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

public static class WmsEndpoints
{
    public static IEndpointRouteBuilder MapWmsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Tenant en la ruta: la política frena a quien no tiene el permiso y el handler (WmsAuthorization) a quien lo tiene en
        // otro tenant. Las demás rutas llegan con los casos de uso.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/wms")
            .WithTags("Wms");

        group.MapGet("/warehouses", ListWarehousesAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<WarehousesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> ListWarehousesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        bool includeInactive = false)
    {
        var warehouses = await dispatcher.QueryAsync(new ListWarehousesQuery(tenantId, includeInactive), cancellationToken);

        return Results.Ok(warehouses);
    }
}

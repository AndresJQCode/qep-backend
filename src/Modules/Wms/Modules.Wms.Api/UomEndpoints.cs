using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Las unidades de medida del tenant (spec WMS 2026-10-31 §24.3). Cuelga del grupo <c>/wms</c> de
/// <see cref="WmsEndpoints"/>.</summary>
internal static class UomEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/uoms", ListAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<UomsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/uoms", CreateAsync)
            .RequireAuthorization(WmsPermissions.ProfileManage)
            .Accepts<CreateUomRequest>("application/json")
            .Produces<UomDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListUomsQuery(tenantId), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateUomRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var uom = await dispatcher.SendAsync(CreateUomCommand.From(tenantId, request), cancellationToken);

        // Las unidades no tienen GET por código: el Location apunta al listado, que es donde se ve.
        return Results.Created($"/api/v1/tenants/{tenantId}/wms/uoms", uom);
    }
}

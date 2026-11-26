using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Los dueños de inventario (spec WMS 2026-10-31 §24.3). Cuelga del grupo <c>/wms</c> de <see cref="WmsEndpoints"/>.</summary>
internal static class OwnerEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/owners", ListAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<OwnersDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/owners", CreateAsync)
            .RequireAuthorization(WmsPermissions.OwnerManage)
            .Accepts<CreateOwnerRequest>("application/json")
            .Produces<OwnerDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/owners/{ownerId:guid}", UpdateAsync)
            .RequireAuthorization(WmsPermissions.OwnerManage)
            .Accepts<UpdateOwnerRequest>("application/json")
            .Produces<OwnerDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListOwnersQuery(tenantId), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateOwnerRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var owner = await dispatcher.SendAsync(CreateOwnerCommand.From(tenantId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/wms/owners/{owner.Id}", owner);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid ownerId, UpdateOwnerRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(UpdateOwnerCommand.From(tenantId, ownerId, request), cancellationToken));
}

using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Los perfiles logísticos de variante (spec WMS 2026-10-31 §24.3). Cuelga del grupo <c>/wms</c> de
/// <see cref="WmsEndpoints"/>.</summary>
internal static class VariantProfileEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/variant-profiles", GetAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<VariantProfilesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/variant-profiles/{variantId:guid}", UpdateAsync)
            .RequireAuthorization(WmsPermissions.ProfileManage)
            .Accepts<UpdateVariantProfileRequest>("application/json")
            .Produces<VariantProfileDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    // El lote viaja en la query separado por comas. Un token que no es un Guid se vuelve Guid.Empty y el validador lo rechaza con 422
    // y el campo, en vez de que el binder responda un 400 sin el mapa de errores.
    private static async Task<IResult> GetAsync(
        Guid tenantId, string? variantIds, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var ids = (variantIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => Guid.TryParse(token, out var id) ? id : Guid.Empty)
            .ToList();

        return Results.Ok(await dispatcher.QueryAsync(new GetVariantProfilesQuery(tenantId, ids), cancellationToken));
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid variantId, UpdateVariantProfileRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(UpdateVariantProfileCommand.From(tenantId, variantId, request), cancellationToken));
}

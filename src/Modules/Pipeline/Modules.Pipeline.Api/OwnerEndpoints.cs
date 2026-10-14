using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Api;

/// <summary>D21 y D23 del spec del frontend: los posibles dueños de un negocio, para el selector del formulario. Con 403 el
/// frontend oculta el selector y no reintenta, así que el permiso (<c>pipeline.deal.manage</c>) y los códigos del 403 son contrato.</summary>
internal static class OwnerEndpoints
{
    public static void MapOwnerEndpoints(this RouteGroupBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.MapGet("/owners", ListOwnersAsync)
            .RequireAuthorization(PipelinePermissions.DealManage)
            .Produces<IReadOnlyList<DealOwnerDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ListOwnersAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetDealOwnersQuery(tenantId), cancellationToken));
}

using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Shopify.Application;

namespace Modules.Shopify.Api;

/// <summary>Las rutas de la tienda y de la bandeja (spec 2026-10-15 §7) bajo <c>/api/v1/tenants/{tenantId:guid}/shopify</c>. La
/// tarea 15 trae «Sincronizar ahora»; la 16, la configuración de la tienda y la bandeja. Cada ruta lleva su política y el handler
/// revalida tenant, permiso y módulo (403, nunca 404).</summary>
public static class ShopifyEndpoints
{
    public static IEndpointRouteBuilder MapShopifyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/shopify").WithTags("Shopify");

        group.MapPost("/stores/{connectionId:guid}/sync", SyncStoreAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifySyncResult>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> SyncStoreAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new SyncShopifyStoreCommand(tenantId, connectionId), cancellationToken));
}

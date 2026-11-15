using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Shopify.Application;

namespace Modules.Shopify.Api;

/// <summary>Spec 2026-11-13: las asociaciones de líneas personalizadas de una tienda, bajo
/// <c>/api/v1/tenants/{tenantId:guid}/shopify/stores/{connectionId:guid}/custom-line-mappings</c>. Lleva los permisos de la bandeja
/// (<c>quotations.order.read</c> / <c>manage</c>, cuyas políticas ya existen) y el handler revalida tenant, permiso, módulo y
/// conexión (403, nunca 404). Archivo propio para no chocar con las otras ramas de Shopify.</summary>
public static class ShopifyCustomLineMappingEndpoints
{
    public static IEndpointRouteBuilder MapShopifyCustomLineMappingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/shopify/stores/{connectionId:guid}/custom-line-mappings")
            .WithTags("Shopify");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(ShopifyPermissions.OrderRead)
            .Produces<IReadOnlyList<ShopifyCustomLineMappingDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifyCustomLineMappingDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListShopifyCustomLineMappingsQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId,
        Guid connectionId,
        CreateShopifyCustomLineMappingRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var mapping = await dispatcher.SendAsync(
            new CreateShopifyCustomLineMappingCommand(tenantId, connectionId, request.Title, request.VariantId ?? Guid.Empty),
            cancellationToken);
        return Results.Created(
            $"/api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/custom-line-mappings/{mapping.Id}", mapping);
    }

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid connectionId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteShopifyCustomLineMappingCommand(tenantId, connectionId, id), cancellationToken);
        return Results.NoContent();
    }
}

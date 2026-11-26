using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modules.Shopify.Application;

namespace Modules.Shopify.Api;

/// <summary>
/// Spec 2026-11-17 §7: los productos desde Shopify, bajo <c>/api/v1/tenants/{tenantId:guid}/shopify</c>. Llevan los permisos de
/// integraciones (<c>integrations.connection.read</c> / <c>manage</c>, cuyas políticas ya existen) y cada handler revalida tenant,
/// permiso, módulo y conexión (403, nunca 404). Archivo propio para no chocar con las otras ramas de Shopify.
/// </summary>
public static class ShopifyProductEndpoints
{
    public static IEndpointRouteBuilder MapShopifyProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/shopify").WithTags("Shopify");

        group.MapPost("/stores/{connectionId:guid}/products/import", StartImportAsync)
            .RequireAuthorization(ShopifyPermissions.ConnectionManage)
            .Produces<ShopifyProductImportStarted>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/stores/{connectionId:guid}/products/import", GetImportAsync)
            .RequireAuthorization(ShopifyPermissions.ConnectionRead)
            .Produces<ShopifyProductImportDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/stores/{connectionId:guid}/products/coverage", GetCoverageAsync)
            .RequireAuthorization(ShopifyPermissions.ConnectionRead)
            .Produces<ShopifyProductCoverageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Spec 2026-11-17 (onboarding) §4.2: «Seguir así» con la cobertura que hay.
        group.MapPost("/stores/{connectionId:guid}/products/coverage/accept", AcceptCoverageAsync)
            .RequireAuthorization(ShopifyPermissions.ConnectionManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapDelete("/products/{productId:guid}/link", UnlinkAsync)
            .RequireAuthorization(ShopifyPermissions.ConnectionManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> StartImportAsync(
        Guid tenantId,
        Guid connectionId,
        StartShopifyProductImportRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var started = await dispatcher.SendAsync(
            new StartShopifyProductImportCommand(tenantId, connectionId, request.Mode, request.RedirectUri), cancellationToken);
        return Results.Accepted($"/api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/products/import", started);
    }

    // Sin importaciones el cuerpo es el literal `null`: Results.Json(null) no escribe nada y la pantalla recibiría un cuerpo vacío.
    private static async Task<IResult> GetImportAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var import = await dispatcher.QueryAsync(new GetShopifyProductImportQuery(tenantId, connectionId), cancellationToken);
        return import is null ? Results.Content("null", "application/json") : Results.Ok(import);
    }

    private static async Task<IResult> GetCoverageAsync(
        Guid tenantId,
        Guid connectionId,
        [FromQuery] string? redirectUri,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetShopifyProductCoverageQuery(tenantId, connectionId, redirectUri), cancellationToken));

    private static async Task<IResult> AcceptCoverageAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new AcceptShopifyProductCoverageCommand(tenantId, connectionId), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UnlinkAsync(
        Guid tenantId, Guid productId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new UnlinkShopifyProductCommand(tenantId, productId), cancellationToken);
        return Results.NoContent();
    }
}

using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modules.Shopify.Application;

namespace Modules.Shopify.Api;

/// <summary>Spec 2026-11-14 §6: los cambios de Shopify bajo <c>/api/v1/tenants/{tenantId:guid}/shopify</c>: «Cambios por revisar»,
/// los cambios de un pedido del espejo y aceptar, rechazar o revisar uno. Leer pide <c>quotations.order.read</c> y decidir
/// <c>quotations.order.manage</c> (las políticas ya existen); el handler revalida tenant, permiso y módulo (403, nunca 404).</summary>
public static class ShopifyOrderEditEndpoints
{
    public static IEndpointRouteBuilder MapShopifyOrderEditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/shopify").WithTags("Shopify");

        group.MapGet("/changes", ListChangesAsync)
            .RequireAuthorization(ShopifyPermissions.OrderRead)
            .Produces<ShopifyOrderChangePageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/orders/{orderId:guid}/changes", ListOrderChangesAsync)
            .RequireAuthorization(ShopifyPermissions.OrderRead)
            .Produces<IReadOnlyList<ShopifyOrderChangeDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        MapDecision(group, "accept", (tenantId, changeId) => new AcceptShopifyChangeCommand(tenantId, changeId))
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
        MapDecision(group, "reject", (tenantId, changeId) => new RejectShopifyChangeCommand(tenantId, changeId));
        MapDecision(group, "review", (tenantId, changeId) => new ReviewShopifyChangeCommand(tenantId, changeId));
        return endpoints;
    }

    private static RouteHandlerBuilder MapDecision(
        RouteGroupBuilder group, string action, Func<Guid, Guid, ICommand<ShopifyOrderChangeDto>> command) =>
        group.MapPost(
                $"/changes/{{changeId:guid}}/{action}",
                async (Guid tenantId, Guid changeId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                    Results.Ok(await dispatcher.SendAsync(command(tenantId, changeId), cancellationToken)))
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifyOrderChangeDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    // Como GET /orders: status acepta varios, repetido (?status=Pending&status=Blocked) o separado por comas.
    private static async Task<IResult> ListChangesAsync(
        Guid tenantId,
        [FromQuery(Name = "status")] string[]? status,
        Guid? connectionId,
        int? page,
        int? pageSize,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var statuses = status?
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        return Results.Ok(await dispatcher.QueryAsync(
            new ListShopifyChangesQuery(tenantId, statuses, connectionId, page, pageSize), cancellationToken));
    }

    private static async Task<IResult> ListOrderChangesAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListShopifyOrderChangesQuery(tenantId, orderId), cancellationToken));
}

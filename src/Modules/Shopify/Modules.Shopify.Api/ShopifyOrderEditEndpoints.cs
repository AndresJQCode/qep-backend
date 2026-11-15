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

        // Spec 2026-11-14 §5 (plan D11): el panel de Shopify del detalle del pedido de QEP y la corrección del asesor.
        group.MapGet("/qep-orders/{qepOrderId:guid}", GetSyncAsync)
            .RequireAuthorization(ShopifyPermissions.OrderRead)
            .Produces<ShopifyOrderSyncDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/orders/{orderId:guid}/corrections", CorrectAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifyOrderSyncDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Owner 2026-11-14: soltar las correcciones retenidas por la conexión, de un pedido o de toda la tienda.
        group.MapPost("/orders/{orderId:guid}/push", ReleaseOrderPushAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifyOrderSyncDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/stores/{connectionId:guid}/pushes/release", ReleaseStorePushesAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifyPushReleaseResult>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    private static async Task<IResult> ReleaseOrderPushAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReleaseShopifyOrderPushCommand(tenantId, orderId), cancellationToken));

    private static async Task<IResult> ReleaseStorePushesAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReleaseShopifyStorePushesCommand(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetSyncAsync(
        Guid tenantId, Guid qepOrderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetShopifyOrderSyncQuery(tenantId, qepOrderId), cancellationToken));

    // El If-Match es la versión del pedido de QEP (OrderDto.version), no la de la fila del espejo: lo que se protege es el pedido.
    private static async Task<IResult> CorrectAsync(
        Guid tenantId,
        Guid orderId,
        CorrectShopifyOrderRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!ShopifyEndpoints.TryParseVersion(httpContext.Request.Headers.IfMatch, out var version))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required", "A valid If-Match header containing the loaded order version is required.");
        }

        return Results.Ok(await dispatcher.SendAsync(
            new CorrectShopifyOrderCommand(tenantId, orderId, version, request.ShippingParty, request.BuyerNotes, request.Attributes),
            cancellationToken));
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

/// <summary>El cuerpo de <c>PUT /orders/{id}/corrections</c> (spec 2026-11-14 §5). Todo opcional en el binding para que un campo
/// ausente sea un 422 con <c>errors</c> del validador y no un 400 del deserializador.</summary>
public sealed record CorrectShopifyOrderRequest(
    ShopifyPartyInput? ShippingParty, string? BuyerNotes, IReadOnlyList<ShopifyAttributeInput>? Attributes);

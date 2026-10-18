using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modules.Shopify.Application;

namespace Modules.Shopify.Api;

/// <summary>Las rutas de la tienda y de la bandeja (spec 2026-10-15 §7) bajo <c>/api/v1/tenants/{tenantId:guid}/shopify</c>: la
/// configuración de la tienda, la bandeja y «Sincronizar ahora». Cada ruta lleva su política y el handler
/// revalida tenant, permiso y módulo (403, nunca 404).</summary>
public static class ShopifyEndpoints
{
    public static IEndpointRouteBuilder MapShopifyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/shopify").WithTags("Shopify");

        group.MapGet("/stores", ListStoresAsync)
            .RequireAuthorization(ShopifyPermissions.ConnectionRead)
            .Produces<IReadOnlyList<ShopifyStoreSummaryDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/stores/{connectionId:guid}", GetStoreAsync)
            .RequireAuthorization(ShopifyPermissions.ConnectionRead)
            .Produces<ShopifyStoreDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/stores/{connectionId:guid}", UpdateStoreAsync)
            .RequireAuthorization(ShopifyPermissions.ConnectionManage)
            .Produces<ShopifyStoreDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/stores/{connectionId:guid}/sync", SyncStoreAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifySyncResult>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/orders", ListOrdersAsync)
            .RequireAuthorization(ShopifyPermissions.OrderRead)
            .Produces<ShopifyOrderPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/orders/summary", SummaryAsync)
            .RequireAuthorization(ShopifyPermissions.OrderRead)
            .Produces<ShopifyOrdersSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/orders/{orderId:guid}/retry", RetryAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifyOrderRowDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/orders/{orderId:guid}/warnings/review", ReviewAsync)
            .RequireAuthorization(ShopifyPermissions.OrderManage)
            .Produces<ShopifyOrderRowDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListStoresAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListShopifyStoresQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetStoreAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var store = await dispatcher.QueryAsync(new GetShopifyStoreQuery(tenantId, connectionId), cancellationToken);
        httpContext.Response.Headers.ETag = $"\"{store.Version}\"";
        return Results.Ok(store);
    }

    // Mismo contrato que el resto del repo: sin If-Match 428, versión vieja 412 (en el handler), y el ETag trae la nueva.
    private static async Task<IResult> UpdateStoreAsync(
        Guid tenantId,
        Guid connectionId,
        UpdateShopifyStoreRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var store = await dispatcher.SendAsync(
            new UpdateShopifyStoreCommand(
                tenantId,
                connectionId,
                request.AdvisorMembershipId ?? Guid.Empty,
                request.BillingCompanyId ?? Guid.Empty,
                request.BillingBankName,
                request.BillingAccountNumber,
                request.BillingCurrency,
                request.DefaultClassificationId,
                request.DocumentAttributeKey,
                request.DocumentTypeAttributeKey,
                request.DefaultIdentificationType,
                RequireVersion(httpContext)),
            cancellationToken);
        httpContext.Response.Headers.ETag = $"\"{store.Version}\"";
        return Results.Ok(store);
    }

    private static async Task<IResult> SyncStoreAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new SyncShopifyStoreCommand(tenantId, connectionId), cancellationToken));

    // §7: state acepta varios, repetido (?state=Waiting&state=Ignored) o separado por comas (?state=Waiting,Ignored).
    private static async Task<IResult> ListOrdersAsync(
        Guid tenantId,
        [FromQuery(Name = "state")] string[]? state,
        Guid? connectionId,
        bool? withWarnings,
        int? page,
        int? pageSize,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var states = state?
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        return Results.Ok(await dispatcher.QueryAsync(
            new ListShopifyOrdersQuery(tenantId, states, connectionId, withWarnings, page, pageSize), cancellationToken));
    }

    private static async Task<IResult> SummaryAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetShopifyOrdersSummaryQuery(tenantId), cancellationToken));

    private static async Task<IResult> RetryAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new RetryShopifyOrderCommand(tenantId, orderId), cancellationToken));

    private static async Task<IResult> ReviewAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ReviewShopifyOrderWarningsCommand(tenantId, orderId), cancellationToken));

    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded store version is required.");

    // Acepta lo que la SPA ya manda en el resto del repo: "7" y también el débil W/"7".
    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}

/// <summary>El cuerpo de <c>PUT /stores/{connectionId}</c> (§6.1). Todo opcional en el binding para que un campo ausente sea un
/// 422 con <c>errors</c> del validador y no un 400 del deserializador.</summary>
public sealed record UpdateShopifyStoreRequest(
    Guid? AdvisorMembershipId,
    Guid? BillingCompanyId,
    string? BillingBankName,
    string? BillingAccountNumber,
    string? BillingCurrency,
    Guid? DefaultClassificationId,
    string? DocumentAttributeKey,
    string? DocumentTypeAttributeKey,
    string? DefaultIdentificationType);

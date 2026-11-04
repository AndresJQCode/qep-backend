using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Store.Application;

namespace Modules.Store.Api;

public static class StoreEndpoints
{
    public static IEndpointRouteBuilder MapStoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como loans y billing: la política frena a quien no tiene el permiso y el handler
        // (StoreAuthorization) a quien lo tiene en otro tenant. Las rutas de la bandeja llegan con su tarea.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/store")
            .WithTags("Store");

        group.MapGet("/settings", GetSettingsAsync)
            .RequireAuthorization(StorePermissions.SettingsManage)
            .Produces<StoreSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/settings", UpdateSettingsAsync)
            .RequireAuthorization(StorePermissions.SettingsManage)
            .Produces<StoreSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/settings/publish", PublishAsync)
            .RequireAuthorization(StorePermissions.SettingsManage)
            .Produces<StoreSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/settings/unpublish", UnpublishAsync)
            .RequireAuthorization(StorePermissions.SettingsManage)
            .Produces<StoreSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Sin If-Match: no escribe nada (como el preview de Loans).
        group.MapPost("/settings/colors/preview", PreviewColorsAsync)
            .RequireAuthorization(StorePermissions.SettingsManage)
            .Produces<StoreColorPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/settings/billing-accounts", ListBillingAccountsAsync)
            .RequireAuthorization(StorePermissions.SettingsManage)
            .Produces<StoreBillingAccountListDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // La bandeja «Desde la tienda»: quotations.order.manage (el permiso de Pedidos) y, en el handler, el módulo `store`.
        var inbox = group.MapGroup("/order-requests");

        inbox.MapGet("/", ListOrderRequestsAsync)
            .RequireAuthorization(StorePermissions.OrderManage)
            .Produces<StoreOrderRequestPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        inbox.MapGet("/summary", GetOrderRequestSummaryAsync)
            .RequireAuthorization(StorePermissions.OrderManage)
            .Produces<StoreInboxSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        inbox.MapGet("/{requestId:guid}", GetOrderRequestAsync)
            .RequireAuthorization(StorePermissions.OrderManage)
            .Produces<StoreOrderRequestDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        inbox.MapGet("/{requestId:guid}/customer-candidates", GetCustomerCandidatesAsync)
            .RequireAuthorization(StorePermissions.OrderManage)
            .Produces<StoreCustomerCandidatesDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Sin If-Match: el estado de la solicitud (y su lease) es la comprobación de concurrencia.
        inbox.MapPost("/{requestId:guid}/take", TakeOrderRequestAsync)
            .RequireAuthorization(StorePermissions.OrderManage)
            .Produces<StoreTakeResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        inbox.MapPost("/{requestId:guid}/discard", DiscardOrderRequestAsync)
            .RequireAuthorization(StorePermissions.OrderManage)
            .Produces<StoreOrderRequestRowDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Las rutas públicas las cablea Program.cs con sus limitadores (MapPublicStoreEndpoints).
        return endpoints;
    }

    private static async Task<IResult> ListOrderRequestsAsync(
        Guid tenantId, string? view, int? page, int? pageSize, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListOrderRequestsQuery(tenantId, view, page, pageSize), cancellationToken));

    private static async Task<IResult> GetOrderRequestSummaryAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetOrderRequestSummaryQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetOrderRequestAsync(
        Guid tenantId, Guid requestId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetOrderRequestQuery(tenantId, requestId), cancellationToken));

    private static async Task<IResult> GetCustomerCandidatesAsync(
        Guid tenantId, Guid requestId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCustomerCandidatesQuery(tenantId, requestId), cancellationToken));

    private static async Task<IResult> TakeOrderRequestAsync(
        Guid tenantId, Guid requestId, TakeOrderRequestBody body, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TakeOrderRequestCommand(tenantId, requestId, body), cancellationToken));

    private static async Task<IResult> DiscardOrderRequestAsync(
        Guid tenantId, Guid requestId, DiscardOrderRequestBody body, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DiscardOrderRequestCommand(tenantId, requestId, body.Reason, body.Note), cancellationToken));

    private static async Task<IResult> GetSettingsAsync(
        Guid tenantId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var settings = await dispatcher.QueryAsync(new GetStoreSettingsQuery(tenantId), cancellationToken);
        return WithETag(httpContext, settings);
    }

    // Mismo contrato que Shopify: sin If-Match 428, versión vieja 412 (en el handler), y el ETag trae la nueva.
    private static async Task<IResult> UpdateSettingsAsync(
        Guid tenantId,
        StoreSettingsRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var settings = await dispatcher.SendAsync(
            new UpdateStoreSettingsCommand(tenantId, RequireVersion(httpContext), request),
            cancellationToken);
        return WithETag(httpContext, settings);
    }

    private static async Task<IResult> PublishAsync(
        Guid tenantId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        WithETag(
            httpContext,
            await dispatcher.SendAsync(new PublishStoreCommand(tenantId, RequireVersion(httpContext), true), cancellationToken));

    private static async Task<IResult> UnpublishAsync(
        Guid tenantId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        WithETag(
            httpContext,
            await dispatcher.SendAsync(new PublishStoreCommand(tenantId, RequireVersion(httpContext), false), cancellationToken));

    private static async Task<IResult> PreviewColorsAsync(
        Guid tenantId, StoreColorPreviewRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new PreviewStoreColorsQuery(tenantId, request.PrimaryColor, request.OnPrimaryColor, request.AccentTextColor),
            cancellationToken));

    private static async Task<IResult> ListBillingAccountsAsync(
        Guid tenantId, string? currency, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListStoreBillingAccountsQuery(tenantId, currency), cancellationToken));

    private static IResult WithETag(HttpContext httpContext, StoreSettingsDto settings)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(settings);
    }

    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded store version is required.");

    // Acepta lo que la SPA ya manda en el resto del repo: "7" y también el débil W/"7". A diferencia de los demás, 0 es válido:
    // es la versión de una tienda que nunca se guardó, y con ella se crea.
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
        return long.TryParse(normalized, out version) && version >= 0;
    }
}

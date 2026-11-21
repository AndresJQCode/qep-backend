using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Application.Products;
using Modules.Siigo.Application.Settings;

namespace Modules.Siigo.Api;

/// <summary>
/// Las rutas de Siigo dentro de su tenant (spec 2026-11-18 §5). Tenant en la ruta: la política frena a quien no tiene el permiso (y,
/// con el módulo apagado, <c>ModuleEntitlementMask</c> ya se lo quitó) y el handler (<c>SiigoAuthorization</c>) a quien lo tiene en
/// otro tenant, con 403 y nunca 404. La configuración de una conexión vive bajo
/// <c>/api/v1/tenants/{tenantId}/siigo/connections/{connectionId}</c>, con <c>siigo.settings.manage</c>; los <c>PUT</c> llevan
/// <c>If-Match</c> con la versión (sin él, 428; con una vieja, 412). Los vínculos de productos están bajo <c>…/products</c>; la facturación llega con su tarea.
/// </summary>
public static class SiigoEndpoints
{
    public static IEndpointRouteBuilder MapSiigoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/siigo/connections/{connectionId:guid}")
            .WithTags("Siigo")
            .RequireAuthorization(SiigoPermissions.SettingsManage);

        group.MapGet("/settings", GetSettingsAsync)
            .Produces<SiigoSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/company", AssignCompanyAsync)
            .Produces<SiigoSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPut("/document", UpdateDocumentAsync)
            .Produces<SiigoSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPut("/taxes", UpdateTaxesAsync)
            .Produces<SiigoSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPut("/payments", UpdatePaymentsAsync)
            .Produces<SiigoSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPut("/sellers", UpdateSellersAsync)
            .Produces<SiigoSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/sellers/auto-link", AutoLinkSellersAsync)
            .Produces<SiigoSellerAutoLinkResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/options/{list}", ListOptionsAsync)
            .Produces<IReadOnlyList<SiigoOptionDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Paso 6 (spec 2026-11-18 §5.3): el vínculo de cada variante de QEP con un producto de Siigo. Sin If-Match: el vínculo de una
        // fila no pisa lo que otra pestaña editó en otra fila, y la versión de la configuración sube igual.
        group.MapGet("/products", ListProductsAsync)
            .Produces<SiigoVariantLinksPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // El selector de la modal «Vincular»: los productos activos que Siigo tiene hoy. `refresh=true` se salta la copia de 60 s.
        group.MapGet("/siigo-products", SearchSiigoProductsAsync)
            .Produces<SiigoProductCandidatesPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/products/{variantId:guid}/link", LinkProductAsync)
            .Produces<SiigoVariantLinkDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/products/{variantId:guid}/link", UnlinkProductAsync)
            .Produces<SiigoCoverageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        group.MapPost("/products/auto-link", AutoLinkProductsAsync)
            .Produces<SiigoAutoLinkResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Spec 2026-11-18 §5.4 y §5.6: la factura de un pedido. Cuelga del pedido y no de la conexión (la conexión sale de la empresa de
        // la cuenta de cobro del pedido), con su propio permiso: quien factura no tiene por qué configurar.
        var invoice = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/orders/{orderId:guid}/siigo-invoice")
            .WithTags("Siigo")
            .RequireAuthorization(SiigoPermissions.InvoiceIssue);

        invoice.MapGet("/availability", InvoiceAvailabilityAsync)
            .Produces<SiigoInvoiceAvailabilityDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        invoice.MapGet("/preview", PreviewInvoiceAsync)
            .Produces<SiigoInvoicePreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        invoice.MapGet("/", GetInvoicesAsync)
            .Produces<IReadOnlyList<SiigoInvoiceDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        invoice.MapPost("/", IssueInvoiceAsync)
            .Produces<IReadOnlyList<SiigoInvoiceDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        invoice.MapPost("/stamp", StampInvoiceAsync)
            .Produces<SiigoInvoiceDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        invoice.MapPost("/resolve", ResolveInvoiceAsync)
            .Produces<SiigoInvoiceDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> InvoiceAvailabilityAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new SiigoInvoiceAvailabilityQuery(tenantId, orderId), cancellationToken));

    private static async Task<IResult> PreviewInvoiceAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new PreviewSiigoInvoiceQuery(tenantId, orderId), cancellationToken));

    private static async Task<IResult> GetInvoicesAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetSiigoInvoicesQuery(tenantId, orderId), cancellationToken));

    // El cuerpo es opcional: sin él se usa la propuesta de pagos de la cuenta de cobro.
    private static async Task<IResult> IssueInvoiceAsync(
        Guid tenantId, Guid orderId, SiigoIssueInvoiceRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new IssueSiigoInvoiceCommand(
                tenantId,
                orderId,
                request?.Payments?.Select(payment => new SiigoPaymentChoice(payment.Kind, payment.ProofId ?? Guid.Empty, payment.SiigoPaymentTypeId)).ToArray()),
            cancellationToken));

    private static async Task<IResult> StampInvoiceAsync(
        Guid tenantId, Guid orderId, string? kind, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new StampSiigoInvoiceCommand(tenantId, orderId, kind), cancellationToken));

    private static async Task<IResult> ResolveInvoiceAsync(
        Guid tenantId, Guid orderId, SiigoResolveInvoiceRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ResolveSiigoInvoiceCommand(tenantId, orderId, request.Kind, request.Outcome, request.Number), cancellationToken));

    private static async Task<IResult> ListProductsAsync(
        Guid tenantId,
        Guid connectionId,
        string? search,
        string? filter,
        int? page,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListSiigoVariantLinksQuery(tenantId, connectionId, search, filter, page), cancellationToken));

    private static async Task<IResult> SearchSiigoProductsAsync(
        Guid tenantId,
        Guid connectionId,
        string? search,
        int? page,
        bool? refresh,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new SearchSiigoProductsQuery(tenantId, connectionId, search, page, refresh ?? false), cancellationToken));

    private static async Task<IResult> LinkProductAsync(
        Guid tenantId,
        Guid connectionId,
        Guid variantId,
        SiigoProductLinkRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Json(
            await dispatcher.SendAsync(
                new LinkSiigoVariantCommand(tenantId, connectionId, variantId, request.SiigoProductId, request.Code, request.Name), cancellationToken),
            statusCode: StatusCodes.Status201Created);

    private static async Task<IResult> UnlinkProductAsync(
        Guid tenantId, Guid connectionId, Guid variantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new UnlinkSiigoVariantCommand(tenantId, connectionId, variantId), cancellationToken));

    private static async Task<IResult> AutoLinkProductsAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new AutoLinkSiigoVariantsByCodeCommand(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> GetSettingsAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Respond(httpContext, await dispatcher.QueryAsync(new GetSiigoSettingsQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> AssignCompanyAsync(
        Guid tenantId,
        Guid connectionId,
        SiigoCompanyRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Respond(
            httpContext,
            await dispatcher.SendAsync(
                new AssignSiigoCompanyCommand(tenantId, connectionId, request.CompanyId ?? Guid.Empty, RequireVersion(httpContext)),
                cancellationToken));

    private static async Task<IResult> UpdateDocumentAsync(
        Guid tenantId,
        Guid connectionId,
        SiigoDocumentRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Respond(
            httpContext,
            await dispatcher.SendAsync(
                new UpdateSiigoDocumentCommand(
                    tenantId,
                    connectionId,
                    request.DocumentTypeId,
                    request.CostCenterId,
                    request.WarehouseId,
                    request.SendToDian ?? false,
                    request.SendMail ?? false,
                    request.FreightSeparateInvoice ?? false,
                    request.FreightProductCode,
                    RequireVersion(httpContext)),
                cancellationToken));

    private static async Task<IResult> UpdateTaxesAsync(
        Guid tenantId,
        Guid connectionId,
        SiigoTaxesRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Respond(
            httpContext,
            await dispatcher.SendAsync(
                new UpdateSiigoTaxesCommand(
                    tenantId,
                    connectionId,
                    request.Vat?.Select(link => new SiigoVatLinkInput(link.TaxPercentage ?? -1, link.SiigoTaxId)).ToArray(),
                    request.Withholdings?.Select(link => new SiigoWithholdingLinkInput(link.TaxRateId ?? Guid.Empty, link.SiigoTaxId)).ToArray(),
                    RequireVersion(httpContext)),
                cancellationToken));

    private static async Task<IResult> UpdatePaymentsAsync(
        Guid tenantId,
        Guid connectionId,
        SiigoPaymentsRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Respond(
            httpContext,
            await dispatcher.SendAsync(
                new UpdateSiigoPaymentsCommand(
                    tenantId,
                    connectionId,
                    request.Accounts?.Select(account => new SiigoAccountLinkInput(account.BankAccountKey, account.SiigoPaymentTypeId)).ToArray(),
                    request.CreditPaymentTypeId,
                    request.CreditDays,
                    request.CashOnDeliveryPaymentTypeId,
                    request.DefaultPaymentTypeId,
                    RequireVersion(httpContext)),
                cancellationToken));

    private static async Task<IResult> UpdateSellersAsync(
        Guid tenantId,
        Guid connectionId,
        SiigoSellersRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Respond(
            httpContext,
            await dispatcher.SendAsync(
                new UpdateSiigoSellersCommand(
                    tenantId,
                    connectionId,
                    request.Sellers?.Select(seller => new SiigoSellerLinkInput(seller.MembershipId, seller.SiigoUserId, seller.SiigoUserName)).ToArray(),
                    RequireVersion(httpContext)),
                cancellationToken));

    private static async Task<IResult> AutoLinkSellersAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new AutoLinkSiigoSellersByEmailCommand(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> ListOptionsAsync(
        Guid tenantId, Guid connectionId, string list, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListSiigoOptionsQuery(tenantId, connectionId, list), cancellationToken));

    // El ETag es la versión: lo que la pantalla manda de vuelta en If-Match.
    private static IResult Respond(HttpContext httpContext, SiigoSettingsDto settings)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(settings);
    }

    // Mismo contrato que el resto del repo: sin If-Match 428, versión vieja 412 (en el handler), y el ETag trae la nueva.
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded settings version is required.");

    // Acepta lo que la SPA ya manda en el resto del repo: "7" y también el débil W/"7". A diferencia de otras entidades, la
    // configuración de Siigo nace en versión 0, así que el 0 es una versión válida.
    internal static bool TryParseVersion(string? etag, out long version)
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

/// <summary>El cuerpo de <c>PUT …/products/{variantId}/link</c>: el producto de Siigo elegido (id, código y nombre).</summary>
public sealed record SiigoProductLinkRequest(string? SiigoProductId, string? Code, string? Name);

/// <summary>El cuerpo de <c>PUT …/company</c>. Opcional en el binding: una empresa que falta es un 422 con <c>errors</c>.</summary>
public sealed record SiigoCompanyRequest(Guid? CompanyId);

/// <summary>El cuerpo de <c>PUT …/document</c>. Los interruptores ausentes valen <c>false</c>, como de fábrica. Un <c>sellerId</c> (frontend
/// anterior a la spec 2026-11-20) se ignora: el vendedor es el asesor de cada pedido.</summary>
public sealed record SiigoDocumentRequest(
    string? DocumentTypeId,
    string? CostCenterId,
    string? WarehouseId,
    bool? SendToDian,
    bool? SendMail,
    bool? FreightSeparateInvoice,
    string? FreightProductCode);

public sealed record SiigoVatLinkRequest(int? TaxPercentage, string? SiigoTaxId);

public sealed record SiigoWithholdingLinkRequest(Guid? TaxRateId, string? SiigoTaxId);

/// <summary>El cuerpo de <c>PUT …/taxes</c>: las dos listas vuelven enteras.</summary>
public sealed record SiigoTaxesRequest(IReadOnlyList<SiigoVatLinkRequest>? Vat, IReadOnlyList<SiigoWithholdingLinkRequest>? Withholdings);

public sealed record SiigoAccountLinkRequest(string? BankAccountKey, string? SiigoPaymentTypeId);

/// <summary>Una fila del cuerpo de <c>PUT …/sellers</c>.</summary>
public sealed record SiigoSellerLinkRequest(Guid MembershipId, string? SiigoUserId, string? SiigoUserName);

/// <summary>El cuerpo de <c>PUT …/sellers</c> (spec 2026-11-20 §4.2).</summary>
public sealed record SiigoSellersRequest(IReadOnlyList<SiigoSellerLinkRequest>? Sellers);

/// <summary>El cuerpo de <c>PUT …/payments</c>.</summary>
public sealed record SiigoPaymentsRequest(
    IReadOnlyList<SiigoAccountLinkRequest>? Accounts,
    string? CreditPaymentTypeId,
    int? CreditDays,
    string? CashOnDeliveryPaymentTypeId,
    string? DefaultPaymentTypeId);

/// <summary>Una forma de pago elegida para un comprobante. Todo opcional en el binding: lo que falta es un 422 con <c>errors</c>.</summary>
public sealed record SiigoPaymentChoiceRequest(string? Kind, Guid? ProofId, string? SiigoPaymentTypeId);

/// <summary>El cuerpo opcional de <c>POST …/siigo-invoice</c>.</summary>
public sealed record SiigoIssueInvoiceRequest(IReadOnlyList<SiigoPaymentChoiceRequest>? Payments);

/// <summary>El cuerpo de <c>POST …/siigo-invoice/resolve</c>: <c>exists</c> (con el número) o <c>missing</c>.</summary>
public sealed record SiigoResolveInvoiceRequest(string? Kind, string? Outcome, string? Number);

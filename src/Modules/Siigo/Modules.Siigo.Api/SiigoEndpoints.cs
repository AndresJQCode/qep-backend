using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Settings;

namespace Modules.Siigo.Api;

/// <summary>
/// Las rutas de Siigo dentro de su tenant (spec 2026-11-18 §5). Tenant en la ruta: la política frena a quien no tiene el permiso (y,
/// con el módulo apagado, <c>ModuleEntitlementMask</c> ya se lo quitó) y el handler (<c>SiigoAuthorization</c>) a quien lo tiene en
/// otro tenant, con 403 y nunca 404. La configuración de una conexión vive bajo
/// <c>/api/v1/tenants/{tenantId}/siigo/connections/{connectionId}</c>, con <c>siigo.settings.manage</c>; los <c>PUT</c> llevan
/// <c>If-Match</c> con la versión (sin él, 428; con una vieja, 412). Los vínculos de productos y la facturación llegan con sus tareas.
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

        group.MapGet("/options/{list}", ListOptionsAsync)
            .Produces<IReadOnlyList<SiigoOptionDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

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
                    request.SellerId,
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

/// <summary>El cuerpo de <c>PUT …/company</c>. Opcional en el binding: una empresa que falta es un 422 con <c>errors</c>.</summary>
public sealed record SiigoCompanyRequest(Guid? CompanyId);

/// <summary>El cuerpo de <c>PUT …/document</c>. Los interruptores ausentes valen <c>false</c>, como de fábrica.</summary>
public sealed record SiigoDocumentRequest(
    string? DocumentTypeId,
    string? SellerId,
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

/// <summary>El cuerpo de <c>PUT …/payments</c>.</summary>
public sealed record SiigoPaymentsRequest(
    IReadOnlyList<SiigoAccountLinkRequest>? Accounts,
    string? CreditPaymentTypeId,
    int? CreditDays,
    string? CashOnDeliveryPaymentTypeId,
    string? DefaultPaymentTypeId);

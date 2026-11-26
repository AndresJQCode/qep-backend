using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// The tenant's minimum purchase (spec 2026-10-08, D7). Settings permissions and not a new one,
/// the same pair as OrdersExportLayoutEndpoints. The whole document travels both ways: the form
/// repaints what comes back.
/// </summary>
public static class QuotationSettingsEndpoints
{
    public static IEndpointRouteBuilder MapQuotationSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations/settings")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<QuotationSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateQuotationSettingsRequest>("application/json")
            .Produces<QuotationSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // El contenido configurable del PDF (spec 2026-11-16 §4.2): endpoint aparte, porque el cuerpo de PUT /settings lo manda
        // entero otra pantalla y sumarle campos obligatorios la rompería. Mismos permisos de settings.
        group.MapGet("/pdf-content", GetPdfContentAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<PdfContentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/pdf-content", UpdatePdfContentAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdatePdfContentRequest>("application/json")
            .Produces<PdfContentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.QueryAsync(new GetQuotationSettingsQuery(tenantId), cancellationToken)));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateQuotationSettingsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.SendAsync(
            new UpdateQuotationSettingsCommand(tenantId, request.MinimumUnits, request.MinimumTotals),
            cancellationToken)));

    private static async Task<IResult> GetPdfContentAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPdfContentQuery(tenantId), cancellationToken));

    private static async Task<IResult> UpdatePdfContentAsync(
        Guid tenantId,
        UpdatePdfContentRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdatePdfContentCommand(
                tenantId, request.PaymentNotice, request.Terms, request.DeliveryProcess, request.PaymentAccount),
            cancellationToken));

    private static QuotationSettingsResponse ToResponse(QuotationSettingsDto settings) =>
        new(settings.MinimumUnits, settings.MinimumTotals);
}

/// <summary><c>minimumTotals</c> nullable on purpose: a body without it reaches the validator
/// as null and is a 422, instead of silently deleting every total.</summary>
public sealed record UpdateQuotationSettingsRequest(
    int MinimumUnits,
    IReadOnlyDictionary<string, decimal>? MinimumTotals);

public sealed record QuotationSettingsResponse(
    int MinimumUnits,
    IReadOnlyDictionary<string, decimal> MinimumTotals);

/// <summary>Las tres secciones completas y la cuenta para el pago, sin <c>variables</c>. Nullable a propósito: una sección que falta llega al validador como
/// null y es un 422, no un borrado en silencio.</summary>
public sealed record UpdatePdfContentRequest(
    PdfSectionDto? PaymentNotice,
    PdfSectionDto? Terms,
    PdfSectionDto? DeliveryProcess,
    PdfPaymentAccountDto? PaymentAccount);

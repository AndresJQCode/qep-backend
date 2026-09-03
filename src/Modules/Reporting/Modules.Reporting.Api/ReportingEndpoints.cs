using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Reporting.Application;

namespace Modules.Reporting.Api;

/// <summary>
/// Los ocho endpoints de reportes: cuatro listados paginados y sus cuatro exportaciones.
///
/// **La exportacion devuelve el archivo en el cuerpo**, no el 202 + correo que usa
/// <c>customers/export</c>. La diferencia es deliberada y esta en el contrato: aquella exporta el
/// padron entero de un tenant y puede tardar, mientras que un reporte ya viene acotado por sus
/// filtros y tiene tope de filas, asi que la descarga directa es lo correcto.
/// </summary>
public static class ReportingEndpoints
{
    // El MIME oficial de .xlsx (OOXML SpreadsheetML), el mismo que usa CustomerEndpoints.
    // ClosedXML solo escribe este formato, nunca el .xls binario viejo.
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapReportingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/reports")
            .WithTags("Reporting");

        group.MapGet("/sales", ListSalesAsync)
            .RequireAuthorization(ReportingPermissions.SalesRead)
            .Produces<ReportPage<SalesReportItemDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/sales/export", ExportSalesAsync)
            .RequireAuthorization(ReportingPermissions.SalesRead)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/quotations", ListQuotationsAsync)
            .RequireAuthorization(ReportingPermissions.QuotationRead)
            .Produces<ReportPage<QuotationsReportItemDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/quotations/export", ExportQuotationsAsync)
            .RequireAuthorization(ReportingPermissions.QuotationRead)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/price-changes", ListPriceChangesAsync)
            .RequireAuthorization(ReportingPermissions.PriceChangeRead)
            .Produces<ReportPage<PriceChangeReportItemDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/price-changes/export", ExportPriceChangesAsync)
            .RequireAuthorization(ReportingPermissions.PriceChangeRead)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/customers", ListCustomersAsync)
            .RequireAuthorization(ReportingPermissions.CustomerRead)
            .Produces<ReportPage<CustomerReportItemDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/customers/export", ExportCustomersAsync)
            .RequireAuthorization(ReportingPermissions.CustomerRead)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListSalesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? advisorId = null,
        Guid? clientId = null,
        string? paymentStatus = null,
        int page = 1,
        int pageSize = ReportPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListSalesReportQuery(
                new SalesReportFilter(tenantId, from, to, advisorId, clientId, paymentStatus),
                page,
                pageSize),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ExportSalesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? advisorId = null,
        Guid? clientId = null,
        string? paymentStatus = null)
    {
        var file = await dispatcher.QueryAsync(
            new ExportSalesReportQuery(
                new SalesReportFilter(tenantId, from, to, advisorId, clientId, paymentStatus)),
            cancellationToken);

        return Results.File(file.Content, ExcelContentType, file.FileName);
    }

    private static async Task<IResult> ListQuotationsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? advisorId = null,
        Guid? clientId = null,
        string? status = null,
        int page = 1,
        int pageSize = ReportPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListQuotationsReportQuery(
                new QuotationsReportFilter(tenantId, from, to, advisorId, clientId, status),
                page,
                pageSize),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ExportQuotationsAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? advisorId = null,
        Guid? clientId = null,
        string? status = null)
    {
        var file = await dispatcher.QueryAsync(
            new ExportQuotationsReportQuery(
                new QuotationsReportFilter(tenantId, from, to, advisorId, clientId, status)),
            cancellationToken);

        return Results.File(file.Content, ExcelContentType, file.FileName);
    }

    private static async Task<IResult> ListPriceChangesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? productId = null,
        Guid? changedBy = null,
        string? field = null,
        int page = 1,
        int pageSize = ReportPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListPriceChangeReportQuery(
                new PriceChangeReportFilter(tenantId, from, to, productId, changedBy, field),
                page,
                pageSize),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ExportPriceChangesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? productId = null,
        Guid? changedBy = null,
        string? field = null)
    {
        var file = await dispatcher.QueryAsync(
            new ExportPriceChangeReportQuery(
                new PriceChangeReportFilter(tenantId, from, to, productId, changedBy, field)),
            cancellationToken);

        return Results.File(file.Content, ExcelContentType, file.FileName);
    }

    private static async Task<IResult> ListCustomersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        bool? isActive = null,
        Guid? classificationId = null,
        Guid? departmentId = null,
        int page = 1,
        int pageSize = ReportPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListCustomerReportQuery(
                new CustomerReportFilter(tenantId, isActive, classificationId, departmentId),
                page,
                pageSize),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> ExportCustomersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        bool? isActive = null,
        Guid? classificationId = null,
        Guid? departmentId = null)
    {
        var file = await dispatcher.QueryAsync(
            new ExportCustomerReportQuery(
                new CustomerReportFilter(tenantId, isActive, classificationId, departmentId)),
            cancellationToken);

        return Results.File(file.Content, ExcelContentType, file.FileName);
    }
}

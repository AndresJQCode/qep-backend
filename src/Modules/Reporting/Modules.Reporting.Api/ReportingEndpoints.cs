using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Reporting.Application;

namespace Modules.Reporting.Api;

/// <summary>
/// Los ocho endpoints de reportes: cuatro listados paginados y sus cuatro resumenes.
/// </summary>
public static class ReportingEndpoints
{
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

        // Mismo permiso que el listado: expone exactamente los mismos datos, sumados.
        group.MapGet("/sales/summary", GetSalesSummaryAsync)
            .RequireAuthorization(ReportingPermissions.SalesRead)
            .Produces<SalesReportSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/quotations", ListQuotationsAsync)
            .RequireAuthorization(ReportingPermissions.QuotationRead)
            .Produces<ReportPage<QuotationsReportItemDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/quotations/summary", GetQuotationsSummaryAsync)
            .RequireAuthorization(ReportingPermissions.QuotationRead)
            .Produces<QuotationsReportSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/price-changes", ListPriceChangesAsync)
            .RequireAuthorization(ReportingPermissions.PriceChangeRead)
            .Produces<ReportPage<PriceChangeReportItemDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/price-changes/summary", GetPriceChangesSummaryAsync)
            .RequireAuthorization(ReportingPermissions.PriceChangeRead)
            .Produces<PriceChangeReportSummaryDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/customers", ListCustomersAsync)
            .RequireAuthorization(ReportingPermissions.CustomerRead)
            .Produces<ReportPage<CustomerReportItemDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/customers/summary", GetCustomersSummaryAsync)
            .RequireAuthorization(ReportingPermissions.CustomerRead)
            .Produces<CustomerReportSummaryDto>()
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

    /// <summary>
    /// Los mismos filtros que el listado **menos la paginacion**: un resumen de la pagina que se
    /// esta mirando no seria un resumen de nada.
    /// </summary>
    private static async Task<IResult> GetSalesSummaryAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? advisorId = null,
        Guid? clientId = null,
        string? paymentStatus = null)
    {
        var summary = await dispatcher.QueryAsync(
            new GetSalesReportSummaryQuery(
                new SalesReportFilter(tenantId, from, to, advisorId, clientId, paymentStatus)),
            cancellationToken);

        return Results.Ok(summary);
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

    /// <summary>Ver <see cref="GetSalesSummaryAsync"/>: mismos filtros que el listado, sin
    /// paginacion.</summary>
    private static async Task<IResult> GetQuotationsSummaryAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? advisorId = null,
        Guid? clientId = null,
        string? status = null)
    {
        var summary = await dispatcher.QueryAsync(
            new GetQuotationsReportSummaryQuery(
                new QuotationsReportFilter(tenantId, from, to, advisorId, clientId, status)),
            cancellationToken);

        return Results.Ok(summary);
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

    /// <summary>Ver <see cref="GetSalesSummaryAsync"/>: mismos filtros que el listado, sin
    /// paginacion.</summary>
    private static async Task<IResult> GetPriceChangesSummaryAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? productId = null,
        Guid? changedBy = null,
        string? field = null)
    {
        var summary = await dispatcher.QueryAsync(
            new GetPriceChangeReportSummaryQuery(
                new PriceChangeReportFilter(tenantId, from, to, productId, changedBy, field)),
            cancellationToken);

        return Results.Ok(summary);
    }

    /// <summary><c>from</c> y <c>to</c> cortan por fecha de alta: ver
    /// <see cref="CustomerReportFilter"/>.</summary>
    private static async Task<IResult> ListCustomersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        bool? isActive = null,
        Guid? classificationId = null,
        Guid? departmentId = null,
        int page = 1,
        int pageSize = ReportPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListCustomerReportQuery(
                new CustomerReportFilter(
                    tenantId, from, to, isActive, classificationId, departmentId),
                page,
                pageSize),
            cancellationToken);

        return Results.Ok(result);
    }

    /// <summary>Ver <see cref="GetSalesSummaryAsync"/>: mismos filtros que el listado, sin
    /// paginacion.</summary>
    private static async Task<IResult> GetCustomersSummaryAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        bool? isActive = null,
        Guid? classificationId = null,
        Guid? departmentId = null)
    {
        var summary = await dispatcher.QueryAsync(
            new GetCustomerReportSummaryQuery(
                new CustomerReportFilter(
                    tenantId, from, to, isActive, classificationId, departmentId)),
            cancellationToken);

        return Results.Ok(summary);
    }
}

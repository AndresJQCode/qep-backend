using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El listado paginado del reporte de ventas: una fila por venta convertida, con los datos de su
/// cotizacion de origen.
///
/// Los filtros viajan en <see cref="SalesReportFilter"/> y no sueltos en la firma porque la
/// exportacion toma exactamente los mismos (menos la paginacion): compartir el record es lo que
/// hace imposible que los dos caminos se desalineen.
/// </summary>
public sealed record ListSalesReportQuery(SalesReportFilter Filter, int Page, int PageSize)
    : IQuery<ReportPage<SalesReportItemDto>>;

public sealed class ListSalesReportHandler(
    ISalesReportSource source,
    IValidator<SalesReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListSalesReportQuery, ReportPage<SalesReportItemDto>>
{
    public async Task<ReportPage<SalesReportItemDto>> HandleAsync(
        ListSalesReportQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar primero, siempre: antes de validar y antes de tocar ningun origen de datos.
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.SalesRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var page = ReportPaging.NormalizePage(query.Page);
        var pageSize = ReportPaging.NormalizePageSize(query.PageSize);

        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(), page, pageSize, cancellationToken);

        return new ReportPage<SalesReportItemDto>(items, total, page, pageSize);
    }
}

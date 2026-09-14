using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El listado paginado del reporte de pedidos: una fila por pedido convertido, con los datos de su
/// cotizacion de origen.
///
/// Los filtros viajan en <see cref="OrdersReportFilter"/> y no sueltos en la firma porque el
/// resumen toma exactamente los mismos (menos la paginacion): compartir el record es lo que hace
/// imposible que los dos caminos se desalineen.
/// </summary>
public sealed record ListOrdersReportQuery(OrdersReportFilter Filter, int Page, int PageSize)
    : IQuery<ReportPage<OrdersReportItemDto>>;

public sealed class ListOrdersReportHandler(
    IOrdersReportSource source,
    IValidator<OrdersReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListOrdersReportQuery, ReportPage<OrdersReportItemDto>>
{
    public async Task<ReportPage<OrdersReportItemDto>> HandleAsync(
        ListOrdersReportQuery query,
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

        return new ReportPage<OrdersReportItemDto>(items, total, page, pageSize);
    }
}

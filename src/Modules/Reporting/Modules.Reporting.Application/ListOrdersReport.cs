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
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IMembershipDirectory membershipDirectory)
    : IQueryHandler<ListOrdersReportQuery, ReportPage<OrdersReportItemDto>>
{
    public async Task<ReportPage<OrdersReportItemDto>> HandleAsync(
        ListOrdersReportQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar primero, siempre: antes de validar y antes de tocar ningun origen de datos.
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.OrdersRead);
        // Sin reporting.all_advisors.read, el asesor lo decide quien llama y no la URL. Ver
        // ReportingAuthorization.ScopeAdvisorAsync.
        var filter = query.Filter with
        {
            AdvisorId = await ReportingAuthorization.ScopeAdvisorAsync(
                executionContext,
                membershipDirectory,
                query.Filter.TenantId,
                query.Filter.AdvisorId,
                cancellationToken),
        };
        await validator.ValidateAndThrowAsync(filter, cancellationToken);

        var page = ReportPaging.NormalizePage(query.Page);
        var pageSize = ReportPaging.NormalizePageSize(query.PageSize);

        // El rango se corta en el día del tenant (spec 2026-09-17, punto 4).
        var calendar = await tenantClock.GetAsync(filter.TenantId, cancellationToken);
        var (items, total) = await source.ListAsync(
            filter.ToCriteria(calendar), page, pageSize, cancellationToken);

        return new ReportPage<OrdersReportItemDto>(items, total, page, pageSize);
    }
}

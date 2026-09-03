using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El listado paginado de los cambios de precio del **catalogo de productos**: los dos precios
/// base y el descuento de una escala. No reporta nada de los precios de una linea de cotizacion.
/// </summary>
public sealed record ListPriceChangeReportQuery(
    PriceChangeReportFilter Filter,
    int Page,
    int PageSize) : IQuery<ReportPage<PriceChangeReportItemDto>>;

public sealed class ListPriceChangeReportHandler(
    IPriceChangeReportSource source,
    IValidator<PriceChangeReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListPriceChangeReportQuery, ReportPage<PriceChangeReportItemDto>>
{
    public async Task<ReportPage<PriceChangeReportItemDto>> HandleAsync(
        ListPriceChangeReportQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.PriceChangeRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var page = ReportPaging.NormalizePage(query.Page);
        var pageSize = ReportPaging.NormalizePageSize(query.PageSize);

        var (rows, total) = await source.ListAsync(
            query.Filter.ToCriteria(), page, pageSize, cancellationToken);

        return new ReportPage<PriceChangeReportItemDto>(rows.ToDtos(), total, page, pageSize);
    }
}

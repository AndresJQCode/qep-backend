using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>El listado paginado del reporte de cotizaciones, en cualquiera de sus cinco estados.
/// Ver <see cref="ListSalesReportQuery"/> sobre por que los filtros van en un record
/// aparte.</summary>
public sealed record ListQuotationsReportQuery(
    QuotationsReportFilter Filter,
    int Page,
    int PageSize) : IQuery<ReportPage<QuotationsReportItemDto>>;

public sealed class ListQuotationsReportHandler(
    IQuotationsReportSource source,
    IValidator<QuotationsReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListQuotationsReportQuery, ReportPage<QuotationsReportItemDto>>
{
    public async Task<ReportPage<QuotationsReportItemDto>> HandleAsync(
        ListQuotationsReportQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.QuotationRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var page = ReportPaging.NormalizePage(query.Page);
        var pageSize = ReportPaging.NormalizePageSize(query.PageSize);

        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(), page, pageSize, cancellationToken);

        return new ReportPage<QuotationsReportItemDto>(items, total, page, pageSize);
    }
}

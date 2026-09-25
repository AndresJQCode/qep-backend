using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>El listado paginado del reporte de cotizaciones, en cualquiera de sus cinco estados.
/// Ver <see cref="ListOrdersReportQuery"/> sobre por que los filtros van en un record
/// aparte.</summary>
public sealed record ListQuotationsReportQuery(
    QuotationsReportFilter Filter,
    int Page,
    int PageSize) : IQuery<ReportPage<QuotationsReportItemDto>>;

public sealed class ListQuotationsReportHandler(
    IQuotationsReportSource source,
    IValidator<QuotationsReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IMembershipDirectory membershipDirectory)
    : IQueryHandler<ListQuotationsReportQuery, ReportPage<QuotationsReportItemDto>>
{
    public async Task<ReportPage<QuotationsReportItemDto>> HandleAsync(
        ListQuotationsReportQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.QuotationRead);
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

        return new ReportPage<QuotationsReportItemDto>(items, total, page, pageSize);
    }
}

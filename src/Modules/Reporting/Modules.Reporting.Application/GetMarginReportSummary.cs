using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El resumen del reporte de margen: un objeto por moneda (C10). Mismo filtro, validador y permiso
/// que el listado, sin paginación ni período anterior: el spec no pide comparación.
/// </summary>
public sealed record GetMarginReportSummaryQuery(MarginReportFilter Filter) : IQuery<MarginReportSummaryDto>;

public sealed class GetMarginReportSummaryHandler(
    IMarginReportSource source,
    IValidator<MarginReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetMarginReportSummaryQuery, MarginReportSummaryDto>
{
    public async Task<MarginReportSummaryDto> HandleAsync(
        GetMarginReportSummaryQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.MarginRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var currencies = await source.SummarizeAsync(query.Filter.ToCriteria(calendar), cancellationToken);

        return new MarginReportSummaryDto(currencies);
    }
}

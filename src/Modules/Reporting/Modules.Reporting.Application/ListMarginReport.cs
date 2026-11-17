using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El listado del reporte de margen: una fila por producto y moneda, con sus variantes (spec
/// 2026-11-10 §6.3). Sólo cuentan las líneas con costo (C9): lo que no tiene costo se ve en el resumen.
///
/// Sin alcance de asesor: el margen es un dato de administración, no del trabajo diario de una
/// asesora, y el permiso ya lo restringe.
/// </summary>
public sealed record ListMarginReportQuery(MarginReportFilter Filter, int Page, int PageSize)
    : IQuery<ReportPage<MarginReportProductDto>>;

public sealed class ListMarginReportHandler(
    IMarginReportSource source,
    IValidator<MarginReportFilter> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<ListMarginReportQuery, ReportPage<MarginReportProductDto>>
{
    public async Task<ReportPage<MarginReportProductDto>> HandleAsync(
        ListMarginReportQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar primero, siempre: antes de validar y antes de tocar ningún origen de datos.
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.MarginRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var page = ReportPaging.NormalizePage(query.Page);
        var pageSize = ReportPaging.NormalizePageSize(query.PageSize);

        var calendar = await tenantClock.GetAsync(query.Filter.TenantId, cancellationToken);
        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(calendar), page, pageSize, cancellationToken);

        return new ReportPage<MarginReportProductDto>(items, total, page, pageSize);
    }
}

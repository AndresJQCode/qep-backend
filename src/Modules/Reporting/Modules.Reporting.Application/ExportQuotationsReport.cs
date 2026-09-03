using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>El reporte de cotizaciones en un <c>.xlsx</c>. Ver <see cref="ExportSalesReportQuery"/>
/// sobre por que es una consulta y no un comando.</summary>
public sealed record ExportQuotationsReportQuery(QuotationsReportFilter Filter)
    : IQuery<ReportFile>;

public sealed class ExportQuotationsReportHandler(
    IQuotationsReportSource source,
    IReportExcelBuilder excelBuilder,
    IValidator<QuotationsReportFilter> validator,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<ExportQuotationsReportQuery, ReportFile>
{
    public async Task<ReportFile> HandleAsync(
        ExportQuotationsReportQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.QuotationRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var rows = await source.ListForExportAsync(
            query.Filter.ToCriteria(), ReportExportRules.ExportProbeLimit, cancellationToken);
        ReportExportRules.EnsureExportable(rows.Count);

        return excelBuilder.BuildQuotations(rows, clock.UtcNow, cancellationToken);
    }
}

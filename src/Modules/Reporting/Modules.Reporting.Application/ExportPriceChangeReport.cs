using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>El reporte de cambios de precio en un <c>.xlsx</c>. Ver
/// <see cref="ExportSalesReportQuery"/> sobre por que es una consulta y no un comando.</summary>
public sealed record ExportPriceChangeReportQuery(PriceChangeReportFilter Filter)
    : IQuery<ReportFile>;

public sealed class ExportPriceChangeReportHandler(
    IPriceChangeReportSource source,
    IReportExcelBuilder excelBuilder,
    IValidator<PriceChangeReportFilter> validator,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<ExportPriceChangeReportQuery, ReportFile>
{
    public async Task<ReportFile> HandleAsync(
        ExportPriceChangeReportQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.PriceChangeRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var rows = await source.ListForExportAsync(
            query.Filter.ToCriteria(), ReportExportRules.ExportProbeLimit, cancellationToken);
        ReportExportRules.EnsureExportable(rows.Count);

        return excelBuilder.BuildPriceChanges(rows.ToDtos(), clock.UtcNow, cancellationToken);
    }
}

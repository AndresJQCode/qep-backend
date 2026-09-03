using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El mismo reporte de ventas, en un <c>.xlsx</c> que se devuelve en el cuerpo de la respuesta.
///
/// Es un <see cref="IQuery{TResponse}"/> y no un comando, a diferencia de
/// <c>ExportCustomersCommand</c>: aquel sube un archivo al almacenamiento de objetos, encola un
/// correo y deja auditoria —tiene efecto commiteado—, mientras que este solo lee y arma bytes.
/// El archivo baja directo porque el reporte ya viene acotado por filtros.
/// </summary>
public sealed record ExportSalesReportQuery(SalesReportFilter Filter) : IQuery<ReportFile>;

public sealed class ExportSalesReportHandler(
    ISalesReportSource source,
    IReportExcelBuilder excelBuilder,
    IValidator<SalesReportFilter> validator,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<ExportSalesReportQuery, ReportFile>
{
    public async Task<ReportFile> HandleAsync(
        ExportSalesReportQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.SalesRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var rows = await source.ListForExportAsync(
            query.Filter.ToCriteria(), ReportExportRules.ExportProbeLimit, cancellationToken);
        ReportExportRules.EnsureExportable(rows.Count);

        return excelBuilder.BuildSales(rows, clock.UtcNow, cancellationToken);
    }
}

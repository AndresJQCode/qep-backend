using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>El padron de clientes en un <c>.xlsx</c>. Ver <see cref="ExportSalesReportQuery"/>
/// sobre por que es una consulta y no un comando — y en particular por que no reusa
/// <c>ExportCustomersCommand</c>, que sube el archivo y lo manda por correo.</summary>
public sealed record ExportCustomerReportQuery(CustomerReportFilter Filter) : IQuery<ReportFile>;

public sealed class ExportCustomerReportHandler(
    ICustomerReportSource source,
    IReportExcelBuilder excelBuilder,
    IValidator<CustomerReportFilter> validator,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<ExportCustomerReportQuery, ReportFile>
{
    public async Task<ReportFile> HandleAsync(
        ExportCustomerReportQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.CustomerRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var rows = await source.ListForExportAsync(
            query.Filter.ToCriteria(), ReportExportRules.ExportProbeLimit, cancellationToken);
        ReportExportRules.EnsureExportable(rows.Count);

        return excelBuilder.BuildCustomers(rows, clock.UtcNow, cancellationToken);
    }
}

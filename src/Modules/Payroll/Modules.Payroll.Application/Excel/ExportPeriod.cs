using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record PayrollExportFile(byte[] Content, string FileName);

public sealed record ExportPeriodQuery(Guid TenantId, Guid PeriodId) : IQuery<PayrollExportFile>;

/// <summary>
/// El «Resumen de nómina» de un período, desde Liquidado en adelante. Sale de lo <b>guardado</b> al liquidar (resultados y excluidos
/// de la última liquidación): no consulta a Hr ni a los valores legales, así que un valor nuevo nunca cambia un período cerrado. El
/// archivo dice el estado del período, y exige <c>payroll.period.read</c>.
/// </summary>
public sealed class ExportPeriodHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IPayrollPeriodRepository periods,
    IPayrollResultStore results,
    PayrollCompanies companies,
    PeriodApprovals approvals,
    IPayrollWorkbookBuilder builder)
    : IQueryHandler<ExportPeriodQuery, PayrollExportFile>
{
    public async Task<PayrollExportFile> HandleAsync(ExportPeriodQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, PayrollPermissions.PeriodRead);

        var period = await periods.FindAsync(query.TenantId, new PayrollPeriodId(query.PeriodId), cancellationToken)
            ?? throw PayrollNotFound.Period(query.PeriodId);
        if (period.Status == PayrollPeriodStatus.Draft)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.PeriodNotExportable, "A draft period has nothing to export: liquidate it first.");
        }

        var company = await companies.RequireAsync(query.TenantId, period.CompanyId, cancellationToken);
        var stored = await results.ListAsync(query.TenantId, period.Id, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        // The figures come from what was stored, so a stale file must say it: the period was edited after liquidating, or Hr and the settings
        // moved (only a Liquidado is compared; an approved or closed one is a snapshot).
        var state = await approvals.StateAsync(period, withSource: true, cancellationToken);
        var statusLabel = period.Outdated || state.SourceChanged
            ? $"{PayrollLabels.PeriodStatuses[period.Status]} (desactualizado)"
            : PayrollLabels.PeriodStatuses[period.Status];

        var rows = stored
            .Select(result => new PayrollWorkbookRow(
                result.DocumentType,
                result.DocumentNumber,
                result.FullName,
                result.Position,
                result.Workplace,
                result.Liquidation.WorkedDays,
                result.Liquidation.VacationDays,
                result.Liquidation.Summary))
            .ToList();
        var excluded = PeriodExcluded.Parse(period.ExcludedJson)
            .OrderBy(entry => entry.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.EmployeeId)
            .Select(entry => new PayrollWorkbookExcluded(entry.DocumentType, entry.DocumentNumber, entry.FullName, ReasonLabel(entry.Reason)))
            .ToList();

        var workbook = new PayrollWorkbook(
            company.Name,
            company.TaxId,
            period.StartDate,
            period.EndDate,
            PayrollLabels.Halves[period.Half],
            statusLabel,
            calendar.ToLocal(calendar.UtcNow),
            rows,
            PayrollSummary.Sum(stored.Select(result => result.Liquidation.Summary)),
            excluded);

        return new PayrollExportFile(
            builder.Build(workbook, PayrollColumnLayouts.Summary),
            $"resumen-nomina-{period.StartDate:yyyy-MM-dd}-a-{period.EndDate:yyyy-MM-dd}.xlsx");
    }

    private static string ReasonLabel(string reason) =>
        Enum.TryParse<ExclusionReason>(reason, ignoreCase: false, out var parsed) && PayrollLabels.ExclusionReasons.TryGetValue(parsed, out var label)
            ? label
            : reason;
}

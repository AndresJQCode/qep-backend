using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Payroll.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta las nóminas ya calculadas de Payroll al puerto de Hr (<c>PayrollLayerTests.NoPayrollLayerReferencesHr</c> y
/// <c>HrLayerTests</c>: ninguno de los dos referencia al otro). Una sola lectura de todos los tenants; la regla de «en uso» la aplica
/// Hr en memoria (spec 2026-11-22, «Uso»).
/// </summary>
internal sealed class LegalParameterUsage(IPayrollCalculatedPeriods periods) : ILegalParameterUsage
{
    public async Task<IReadOnlyList<CalculatedPeriodStamp>> ListCalculatedPeriodsOfAllTenantsAsync(
        CancellationToken cancellationToken) =>
        (await periods.ListOfAllTenantsAsync(cancellationToken))
        .Select(period => new CalculatedPeriodStamp(period.LegalValuesDate, period.CompanyId))
        .ToList();
}

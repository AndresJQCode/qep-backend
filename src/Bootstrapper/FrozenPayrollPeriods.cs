using Modules.Hr.Application;
using Modules.Payroll.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta las nóminas congeladas de Payroll al puerto de Hr (<c>PayrollLayerTests.NoPayrollLayerReferencesHr</c> y
/// <c>HrLayerTests</c>: ninguno de los dos referencia al otro). Sólo traduce: la regla de qué bloquea la aplica Hr
/// (spec 2026-11-26, «Nómina»).
/// </summary>
internal sealed class FrozenPayrollPeriods(IPayrollFrozenPeriods periods) : IFrozenPayrollPeriods
{
    public async Task<IReadOnlyList<FrozenPayrollPeriod>> ListForEmployeeAsync(
        Guid tenantId, Guid employeeId, CancellationToken cancellationToken) =>
        (await periods.ListWithResultOfAsync(tenantId, employeeId, cancellationToken))
        .Select(period => new FrozenPayrollPeriod(period.CompanyId, period.StartDate, period.EndDate))
        .ToList();
}

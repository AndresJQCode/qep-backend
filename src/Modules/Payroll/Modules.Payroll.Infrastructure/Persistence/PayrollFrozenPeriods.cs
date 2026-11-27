using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

/// <summary>Una consulta sobre <c>payroll.periods</c> y <c>payroll.employee_results</c>, acotada al tenant y al empleado.</summary>
internal sealed class PayrollFrozenPeriods(PayrollDbContext dbContext) : IPayrollFrozenPeriods
{
    public async Task<IReadOnlyList<PayrollFrozenPeriod>> ListWithResultOfAsync(
        Guid tenantId, Guid employeeId, CancellationToken cancellationToken) =>
        await dbContext.Periods
            .AsNoTracking()
            .Where(period => period.TenantId == tenantId
                && (period.Status == PayrollPeriodStatus.Approved || period.Status == PayrollPeriodStatus.Closed)
                && dbContext.EmployeeResults.Any(result => result.PeriodId == period.Id && result.EmployeeId == employeeId))
            .OrderBy(period => period.StartDate)
            .Select(period => new PayrollFrozenPeriod(period.CompanyId, period.StartDate, period.EndDate))
            .ToListAsync(cancellationToken);
}

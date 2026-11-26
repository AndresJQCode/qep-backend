using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

internal sealed class PayrollResultStore(PayrollDbContext dbContext) : IPayrollResultStore
{
    public async Task<IReadOnlyList<EmployeeResult>> ListAsync(
        Guid tenantId, PayrollPeriodId periodId, CancellationToken cancellationToken)
    {
        var rows = await OfPeriod(tenantId, periodId).AsNoTracking().ToListAsync(cancellationToken);
        return rows
            .OrderBy(row => row.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.EmployeeId)
            .Select(row => row.ToResult())
            .ToList();
    }

    public async Task<EmployeeResult?> FindAsync(
        Guid tenantId, PayrollPeriodId periodId, Guid employeeId, CancellationToken cancellationToken) =>
        (await OfPeriod(tenantId, periodId)
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.EmployeeId == employeeId, cancellationToken))?.ToResult();

    public async Task ReplaceAllAsync(
        Guid tenantId, PayrollPeriodId periodId, IReadOnlyList<EmployeeResult> results, CancellationToken cancellationToken)
    {
        // Se actualiza en su lugar lo que ya existe: borrar y reinsertar la misma pareja (period_id, employee_id) en un solo
        // guardado choca con el índice único según el orden que EF elija.
        var existing = await OfPeriod(tenantId, periodId).ToListAsync(cancellationToken);
        var byEmployee = existing.ToDictionary(row => row.EmployeeId);
        foreach (var result in results)
        {
            if (byEmployee.Remove(result.EmployeeId, out var row))
            {
                row.Apply(result);
            }
            else
            {
                dbContext.EmployeeResults.Add(EmployeeResultRow.From(periodId, result));
            }
        }

        dbContext.EmployeeResults.RemoveRange(byEmployee.Values);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> SumRecurringAppliedAsync(
        Guid tenantId, Guid companyId, Guid employeeId, DateOnly? before, CancellationToken cancellationToken)
    {
        var applied = from row in dbContext.EmployeeResults.AsNoTracking()
                      join period in dbContext.Periods.AsNoTracking() on row.PeriodId equals period.Id
                      where period.TenantId == tenantId
                          && period.CompanyId == companyId
                          && period.Status != PayrollPeriodStatus.Draft
                          && row.EmployeeId == employeeId
                      select new { period.StartDate, row.RecurringAppliedJson };
        if (before is { } limit)
        {
            applied = applied.Where(item => item.StartDate < limit);
        }

        var totals = new Dictionary<Guid, decimal>();
        foreach (var item in await applied.ToListAsync(cancellationToken))
        {
            foreach (var (recurringId, amount) in EmployeeResultRow.ParseApplied(item.RecurringAppliedJson))
            {
                totals[recurringId] = totals.GetValueOrDefault(recurringId) + amount;
            }
        }

        return totals;
    }

    // El tenant se resuelve por el período: los resultados no lo guardan, y un período de otro tenant no devuelve nada.
    private IQueryable<EmployeeResultRow> OfPeriod(Guid tenantId, PayrollPeriodId periodId) =>
        dbContext.EmployeeResults.Where(row => row.PeriodId == periodId
            && dbContext.Periods.Any(period => period.Id == periodId && period.TenantId == tenantId));
}

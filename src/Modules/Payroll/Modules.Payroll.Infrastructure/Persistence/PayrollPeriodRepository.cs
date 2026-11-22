using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

internal sealed class PayrollPeriodRepository(PayrollDbContext dbContext) : IPayrollPeriodRepository
{
    public Task<PayrollPeriod?> FindAsync(Guid tenantId, PayrollPeriodId id, CancellationToken cancellationToken) =>
        dbContext.Periods
            .Include(period => period.Inputs)
            .Include(period => period.Events)
            .AsSplitQuery()
            .SingleOrDefaultAsync(period => period.TenantId == tenantId && period.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PayrollPeriod>> ListAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await dbContext.Periods
            .AsNoTracking()
            .Where(period => period.TenantId == tenantId && period.CompanyId == companyId)
            .OrderByDescending(period => period.StartDate)
            .ToListAsync(cancellationToken);

    public Task<PayrollPeriod?> FindByStartAsync(
        Guid tenantId, Guid companyId, DateOnly start, CancellationToken cancellationToken) =>
        dbContext.Periods
            .AsNoTracking()
            .SingleOrDefaultAsync(
                period => period.TenantId == tenantId && period.CompanyId == companyId && period.StartDate == start,
                cancellationToken);

    public void Add(PayrollPeriod period) => dbContext.Periods.Add(period);

    public async Task<IReadOnlyList<Guid>> ListCompanyIdsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Periods
            .AsNoTracking()
            .Where(period => period.TenantId == tenantId)
            .Select(period => period.CompanyId)
            .Distinct()
            .ToListAsync(cancellationToken);
}

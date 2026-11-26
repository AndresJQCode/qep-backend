using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

internal sealed class CompanyPayrollSettingsRepository(PayrollDbContext dbContext) : ICompanyPayrollSettingsRepository
{
    public Task<CompanyPayrollSettings?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        dbContext.CompanySettings.SingleOrDefaultAsync(
            settings => settings.TenantId == tenantId && settings.CompanyId == companyId, cancellationToken);

    public void Add(CompanyPayrollSettings settings) => dbContext.CompanySettings.Add(settings);

    public async Task<IReadOnlyList<Guid>> ListCompanyIdsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.CompanySettings
            .AsNoTracking()
            .Where(settings => settings.TenantId == tenantId)
            .Select(settings => settings.CompanyId)
            .ToListAsync(cancellationToken);
}

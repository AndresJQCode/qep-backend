using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

internal sealed class RecurringConceptRepository(PayrollDbContext dbContext) : IRecurringConceptRepository
{
    public Task<RecurringConcept?> FindAsync(Guid tenantId, RecurringConceptId id, CancellationToken cancellationToken) =>
        dbContext.RecurringConcepts.SingleOrDefaultAsync(
            recurring => recurring.TenantId == tenantId && recurring.Id == id, cancellationToken);

    public async Task<IReadOnlyList<RecurringConcept>> ListByEmployeeAsync(
        Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken) =>
        await dbContext.RecurringConcepts
            .AsNoTracking()
            .Where(recurring => recurring.TenantId == tenantId && recurring.CompanyId == companyId && recurring.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RecurringConcept>> ListByCompanyAsync(
        Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await dbContext.RecurringConcepts
            .AsNoTracking()
            .Where(recurring => recurring.TenantId == tenantId && recurring.CompanyId == companyId)
            .ToListAsync(cancellationToken);

    public void Add(RecurringConcept recurring) => dbContext.RecurringConcepts.Add(recurring);
}

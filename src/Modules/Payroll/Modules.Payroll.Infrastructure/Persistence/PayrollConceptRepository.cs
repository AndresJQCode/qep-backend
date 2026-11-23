using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Infrastructure.Persistence;

internal sealed class PayrollConceptRepository(PayrollDbContext dbContext) : IPayrollConceptRepository
{
    public Task<PayrollConcept?> FindAsync(
        Guid tenantId, Guid companyId, PayrollConceptId id, CancellationToken cancellationToken) =>
        dbContext.Concepts.SingleOrDefaultAsync(
            concept => concept.TenantId == tenantId && concept.CompanyId == companyId && concept.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PayrollConcept>> ListAsync(
        Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await dbContext.Concepts
            .AsNoTracking()
            .Where(concept => concept.TenantId == tenantId && concept.CompanyId == companyId)
            .ToListAsync(cancellationToken);

    public void Add(PayrollConcept concept) => dbContext.Concepts.Add(concept);
}

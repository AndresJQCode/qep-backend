using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantRepository(TenancyDbContext dbContext) : ITenantRepository
{
    public Task<Tenant?> GetAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        dbContext.Tenants.SingleOrDefaultAsync(
            tenant => tenant.Id == tenantId,
            cancellationToken);

    public async Task<IReadOnlyList<TenantId>> ListAllIdsAsync(CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .AsNoTracking()
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);

    public void Add(Tenant tenant) => dbContext.Tenants.Add(tenant);
}

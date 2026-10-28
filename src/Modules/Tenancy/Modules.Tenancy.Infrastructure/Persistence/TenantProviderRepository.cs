using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantProviderRepository(TenancyDbContext dbContext) : ITenantProviderRepository
{
    public void Add(TenantProvider provider) => dbContext.TenantProviders.Add(provider);

    public async Task<IReadOnlyList<TenantProvider>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.TenantProviders
            .Where(provider => provider.TenantId == tenantId)
            .ToListAsync(cancellationToken);
}

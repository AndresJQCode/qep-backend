using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantModuleRepository(TenancyDbContext dbContext) : ITenantModuleRepository
{
    public void Add(TenantModule tenantModule) => dbContext.TenantModules.Add(tenantModule);

    public async Task<IReadOnlyList<TenantModule>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.TenantModules
            .Where(module => module.TenantId == tenantId)
            .ToListAsync(cancellationToken);
}

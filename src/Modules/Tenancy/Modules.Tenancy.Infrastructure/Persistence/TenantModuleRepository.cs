using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantModuleRepository(TenancyDbContext dbContext) : ITenantModuleRepository
{
    public void Add(TenantModule tenantModule) => dbContext.TenantModules.Add(tenantModule);
}

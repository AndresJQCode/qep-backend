using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantChangeRepository(TenancyDbContext dbContext) : ITenantChangeRepository
{
    public void Add(TenantChange change) => dbContext.TenantChanges.Add(change);
}

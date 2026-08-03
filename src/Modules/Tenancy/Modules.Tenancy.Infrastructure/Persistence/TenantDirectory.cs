using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantDirectory(TenancyDbContext dbContext) : ITenantDirectory
{
    public async Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Slug)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await dbContext.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.TimeZone)
            .SingleOrDefaultAsync(cancellationToken);
}

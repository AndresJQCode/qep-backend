using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantSlugDirectory(TenancyDbContext dbContext) : ITenantSlugDirectory
{
    public async Task<TenantId?> FindIdBySlugAsync(string normalizedSlug, CancellationToken cancellationToken)
    {
        var ids = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Slug == normalizedSlug)
            .Select(tenant => tenant.Id)
            .Take(1)
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? null : ids[0];
    }
}

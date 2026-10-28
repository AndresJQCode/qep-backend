using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenantSlugAvailability(TenancyDbContext dbContext) : ITenantSlugAvailability
{
    public Task<bool> IsTakenAsync(string normalizedSlug, CancellationToken cancellationToken) =>
        dbContext.Tenants.AnyAsync(tenant => tenant.Slug == normalizedSlug, cancellationToken);
}

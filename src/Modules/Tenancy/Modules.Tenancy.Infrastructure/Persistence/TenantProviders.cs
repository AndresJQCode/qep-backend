using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Una sola consulta, sin tracking ni caché, como <see cref="TenantModules"/>: el tenant y sus claves
/// activas. Ordinal, como <see cref="TenantProviderKeys"/> y la columna.
/// </summary>
internal sealed class TenantProviders(TenancyDbContext dbContext) : ITenantProviders
{
    public async Task<IReadOnlySet<string>?> FindEnabledAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var id = new TenantId(tenantId);
        var found = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == id)
            .Select(tenant => new
            {
                Keys = dbContext.TenantProviders
                    .Where(provider => provider.TenantId == tenant.Id && provider.Status == TenantModuleStatus.Active)
                    .Select(provider => provider.ProviderKey)
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return found is null ? null : found.Keys.ToHashSet(StringComparer.Ordinal);
    }
}

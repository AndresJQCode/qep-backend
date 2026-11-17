using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Una sola consulta, sin tracking ni caché entre requests, como <see cref="TenantModules"/>: el tenant y sus claves
/// activas. Ordinal, como <see cref="TenantProviderKeys"/> y la columna. Sí memoiza dentro del scope, también como
/// <see cref="TenantModules"/>: los directorios de conexiones preguntan por cada proveedor que listan (WhatsApp, Zenvia,
/// Instagram) en el mismo request, y eran viajes iguales a la base. Quien habilita un proveedor escribe por el
/// repositorio, no por acá, y su respuesta no relee por este puerto.
/// </summary>
internal sealed class TenantProviders(TenancyDbContext dbContext) : ITenantProviders
{
    private readonly Dictionary<Guid, IReadOnlySet<string>?> _byTenant = [];

    public async Task<IReadOnlySet<string>?> FindEnabledAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_byTenant.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var found = await LoadAsync(tenantId, cancellationToken);
        _byTenant[tenantId] = found;
        return found;
    }

    private async Task<IReadOnlySet<string>?> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
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

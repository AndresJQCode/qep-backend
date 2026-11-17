using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Una sola consulta: el tenant y sus claves, sin tracking. Las claves llegan tipadas por la
/// conversión de EF y van directo a <see cref="TenantModuleSet.FromStored"/>. Sin caché entre requests (spec,
/// alternativa descartada): a lo sumo una fila por módulo del catálogo, por PK; entre requests cachea la
/// resolución de permisos, que es quien la pide siempre. Sí memoiza dentro del scope: la resolución de
/// permisos la pide al autenticar y varios handlers (<c>/modules</c>, <c>/authorization/catalog</c>, la
/// bandeja de Messaging, los de Ai) la vuelven a pedir en el mismo request; eran dos viajes iguales a la base.
/// Quien cambia los módulos escribe por el repositorio, no por acá, y su respuesta no relee por este puerto.
/// </summary>
internal sealed class TenantModules(TenancyDbContext dbContext) : ITenantModules
{
    private readonly Dictionary<Guid, TenantModuleSet?> _byTenant = [];

    public async Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_byTenant.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var found = await LoadAsync(tenantId, cancellationToken);
        _byTenant[tenantId] = found;
        return found;
    }

    private async Task<TenantModuleSet?> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var id = new TenantId(tenantId);
        var found = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == id)
            .Select(tenant => new
            {
                // Spec 2026-10-08 §3: contratado = fila con status = 'active'. FromStored y todo lo de
                // encima (efectivos, missingDependencies, enmascarado, guard, /modules) no cambia.
                Keys = dbContext.TenantModules
                    .Where(module => module.TenantId == tenant.Id && module.Status == TenantModuleStatus.Active)
                    .Select(module => module.ModuleKey)
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return found is null ? null : TenantModuleSet.FromStored(found.Keys);
    }
}

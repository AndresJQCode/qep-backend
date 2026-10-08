using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Una sola consulta: el tenant y sus claves, sin tracking. Las claves llegan tipadas por la
/// conversión de EF y van directo a <see cref="TenantModuleSet.FromStored"/>. Sin caché (spec,
/// alternativa descartada): a lo sumo siete filas por PK, y los permisos ya se resuelven contra la
/// base en cada request; el caché es asunto del adaptador del control plane.
/// </summary>
internal sealed class TenantModules(TenancyDbContext dbContext) : ITenantModules
{
    public async Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var id = new TenantId(tenantId);
        var found = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == id)
            .Select(tenant => new
            {
                Keys = dbContext.TenantModules
                    .Where(module => module.TenantId == tenant.Id)
                    .Select(module => module.ModuleKey)
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return found is null ? null : TenantModuleSet.FromStored(found.Keys);
    }
}

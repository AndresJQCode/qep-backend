using Microsoft.EntityFrameworkCore;
using Modules.Authorization.Application;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Tenancy → Authorization (spec 2026-11-25, roles por tenant): los tenants que la siembra del arranque revisa, en cualquier
/// estado. Un tenant suspendido también necesita sus roles para cuando vuelva.
/// </summary>
internal sealed class TenancyRoleSeedTenantSource(TenancyDbContext tenancy) : IRoleSeedTenantSource
{
    public async Task<IReadOnlyCollection<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken)
    {
        // El id es un value object convertido: se materializa tipado y se desenvuelve fuera de la consulta.
        var ids = await tenancy.Tenants
            .AsNoTracking()
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
        return ids.ConvertAll(id => id.Value);
    }
}

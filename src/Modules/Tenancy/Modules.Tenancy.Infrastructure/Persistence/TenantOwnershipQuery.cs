using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// <see cref="ITenantOwnership"/>: un join de <c>tenants</c> con la membresía owner, sin tracking. Pública, como
/// <see cref="TenantDefaultCurrency"/>, sólo para probar el SQL de <see cref="Owned"/> sin <c>InternalsVisibleTo</c>.
/// </summary>
public sealed class TenantOwnershipQuery(TenancyDbContext dbContext) : ITenantOwnership
{
    public async Task<IReadOnlyList<OwnedTenant>> ListOwnedActiveTenantsAsync(Guid userId, CancellationToken cancellationToken) =>
        await Owned(dbContext, userId).ToListAsync(cancellationToken);

    /// <summary>La consulta: decide qué tenants puede vincular un usuario como distribuidor.</summary>
    public static IQueryable<OwnedTenant> Owned(TenancyDbContext dbContext, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return from tenant in dbContext.Tenants.AsNoTracking()
               join membership in dbContext.Memberships.AsNoTracking() on tenant.OwnerMembershipId equals membership.Id
               // owner_membership_id no tiene FK: la membresía tiene que ser de este mismo tenant, no sólo tener ese id.
               where membership.TenantId == tenant.Id
                   && membership.UserId == userId
                   && membership.State == MembershipState.Active
                   && tenant.Status == TenantStatus.Active
               orderby tenant.DisplayName, tenant.Id
               select new OwnedTenant(tenant.Id.Value, tenant.DisplayName);
    }
}

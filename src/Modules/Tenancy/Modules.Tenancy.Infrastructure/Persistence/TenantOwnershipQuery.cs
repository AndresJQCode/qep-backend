using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary><see cref="ITenantOwnership"/>: un join de <c>tenants</c> con la membresía owner, sin tracking.</summary>
internal sealed class TenantOwnershipQuery(TenancyDbContext dbContext) : ITenantOwnership
{
    public async Task<IReadOnlyList<OwnedTenant>> ListOwnedActiveTenantsAsync(Guid userId, CancellationToken cancellationToken) =>
        await (
                from tenant in dbContext.Tenants.AsNoTracking()
                join membership in dbContext.Memberships.AsNoTracking() on tenant.OwnerMembershipId equals membership.Id
                where membership.UserId == userId
                    && membership.State == MembershipState.Active
                    && tenant.Status == TenantStatus.Active
                orderby tenant.DisplayName, tenant.Id
                select new OwnedTenant(tenant.Id.Value, tenant.DisplayName))
            .ToListAsync(cancellationToken);
}

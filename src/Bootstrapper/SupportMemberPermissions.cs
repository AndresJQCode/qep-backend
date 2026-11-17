using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Plan N4: a member's Support permissions for the app hub, from the same sources as <see cref="SupportMemberDirectory"/>
/// (Tenancy membership + roles → permission through <see cref="IRolePermissionChecker"/>), without cache, so a role removed after
/// the handshake stops the next <c>Subscribe</c>. Only an active membership of the tenant grants anything.
/// </summary>
internal sealed class SupportMemberPermissions(
    TenancyDbContext tenancy,
    IRolePermissionChecker permissions) : ISupportMemberPermissions
{
    public async Task<bool> HasAnyAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<string> required, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var id = new MembershipId(memberId);
        var membership = await tenancy.Memberships.AsNoTracking()
            .Where(candidate => candidate.TenantId == tenant && candidate.Id == id && candidate.State == MembershipState.Active)
            .SingleOrDefaultAsync(cancellationToken);
        if (membership is null)
        {
            return false;
        }

        foreach (var permission in required)
        {
            if (await permissions.AnyGrantsAsync(tenant, membership.Roles, permission, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}

using Modules.Hr.Application;
using Modules.Notifications.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: whether a manager can still read HR data when the digest is about to be sent. Same rule as the
/// settings screen (<c>HrMemberDirectory.ListWithPermissionAsync</c>): the membership must be active and one of its roles must grant
/// <see cref="HrPermissions.EmployeeRead"/>. Asked for a single member, so it does not list the whole tenant.
/// </summary>
internal sealed class HrBirthdayAccess(IMembershipRepository memberships, IRolePermissionChecker permissions) : IHrBirthdayAccess
{
    public async Task<bool> CanReadEmployeesAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var found = await memberships.ListByIdsAsync(tenant, [new MembershipId(memberId)], cancellationToken);
        return found.SingleOrDefault() is { State: MembershipState.Active } membership
            && await permissions.AnyGrantsAsync(tenant, membership.Roles, HrPermissions.EmployeeRead, cancellationToken);
    }
}

using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed class MembershipDirectory(IMembershipRepository membershipRepository)
    : IMembershipDirectory
{
    public async Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var membership = await membershipRepository.FindByUserAndTenantAsync(
            userId,
            new TenantId(tenantId),
            cancellationToken);
        return membership is { State: MembershipState.Active }
            ? membership.Roles
            : null;
    }

    public async Task<Guid?> FindActiveMembershipIdAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var membership = await membershipRepository.FindByUserAndTenantAsync(
            userId,
            new TenantId(tenantId),
            cancellationToken);
        return membership is { State: MembershipState.Active }
            ? membership.Id.Value
            : null;
    }

    public async Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var memberships = await membershipRepository.ListByUserAsync(userId, cancellationToken);
        return memberships.Select(membership => membership.Id.Value).ToList();
    }
}

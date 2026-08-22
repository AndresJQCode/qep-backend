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
}

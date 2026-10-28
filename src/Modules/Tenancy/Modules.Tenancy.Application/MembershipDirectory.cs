using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed class MembershipDirectory(
    IMembershipRepository membershipRepository,
    ITenantDirectory tenantDirectory)
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
        if (membership is not { State: MembershipState.Active })
        {
            return null;
        }

        // Spec 2026-10-08 §4: un tenant que no está Active no resuelve roles. ResolvePermissionsAsync
        // devuelve null, ExternalClaimsTransformation no agrega el claim de tenant y todo endpoint del
        // tenant responde 403, incluso con la sesión ya abierta. Sin fila también es null (fail closed).
        var status = await tenantDirectory.GetStatusAsync(new TenantId(tenantId), cancellationToken);
        return status == TenantStatus.Active ? membership.Roles : null;
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

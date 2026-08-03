using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public interface IMembershipRepository
{
    Task<Membership?> FindByUserAndTenantAsync(
        Guid userId,
        TenantId tenantId,
        CancellationToken cancellationToken);

    Task<Membership?> FindByIdAsync(
        MembershipId id,
        TenantId tenantId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ListInvitedByUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantId>> ListActiveTenantsByUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ListByTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken);

    // Active memberships in the tenant other than excludeId, so a suspend/remove handler
    // can check whether another member still holds a manage-capable role (lockout guard).
    Task<IReadOnlyList<Membership>> ListActiveExcludingAsync(
        TenantId tenantId,
        MembershipId excludeId,
        CancellationToken cancellationToken);

    void Add(Membership membership);
}

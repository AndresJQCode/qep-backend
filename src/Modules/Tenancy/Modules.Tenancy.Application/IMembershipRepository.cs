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

    // Membresías activas del tenant distintas de excludeId, para que un handler de suspender o
    // quitar verifique si otro miembro conserva un rol con capacidad de gestión (guarda de lockout).
    Task<IReadOnlyList<Membership>> ListActiveExcludingAsync(
        TenantId tenantId,
        MembershipId excludeId,
        CancellationToken cancellationToken);

    void Add(Membership membership);
}

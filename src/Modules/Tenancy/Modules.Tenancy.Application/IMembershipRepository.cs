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

    // Resuelve el hash (SHA-256 hex) de un token de invitación a su membresía. El token
    // plano nunca se persiste, así que éste es el único camino de un link de email a una
    // fila; la unicidad la garantiza el índice único de invitation_token_hash.
    Task<Membership?> FindByInvitationTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Membership>> ListInvitedByUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantId>> ListActiveTenantsByUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    // Todas las membresías del usuario en cualquier tenant y en cualquier estado, terminales
    // incluidas. Es la vista que necesita quien decide si un usuario todavía existe para
    // Tenancy (MembershipUserReferenceProbe) o quién más lo referencia (IMembershipDirectory).
    Task<IReadOnlyList<Membership>> ListByUserAsync(
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

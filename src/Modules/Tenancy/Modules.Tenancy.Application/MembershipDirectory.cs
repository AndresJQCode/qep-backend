using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <remarks>
/// Scoped, y memoiza la membresía por <c>(usuario, tenant)</c> dentro del scope, igual que <c>TenantRoleCatalog</c>: la resolución
/// de permisos la busca al autenticar y el handler vuelve a pedirla por <see cref="FindActiveMembershipIdAsync"/> para grabar quién
/// hizo qué. Eran dos consultas iguales por request —la más llamada de toda la base—, y una sola alcanza: la fila es la misma, y
/// si el propio request la modifica es la misma instancia que sigue el <c>DbContext</c> del scope.
/// </remarks>
public sealed class MembershipDirectory(
    IMembershipRepository membershipRepository,
    ITenantDirectory tenantDirectory)
    : IMembershipDirectory
{
    private readonly Dictionary<(Guid UserId, Guid TenantId), Membership?> _memberships = [];

    public async Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var membership = await FindAsync(userId, tenantId, cancellationToken);
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
        var membership = await FindAsync(userId, tenantId, cancellationToken);
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

    private async Task<Membership?> FindAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        var key = (userId, tenantId);
        if (_memberships.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var membership = await membershipRepository.FindByUserAndTenantAsync(
            userId,
            new TenantId(tenantId),
            cancellationToken);
        _memberships[key] = membership;
        return membership;
    }
}

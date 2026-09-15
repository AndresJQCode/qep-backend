using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Tenancy retiene a un usuario mientras conserve una membresía que le da o le promete acceso:
/// <see cref="MembershipState.Invited"/>, <see cref="MembershipState.Active"/> o
/// <see cref="MembershipState.Suspended"/> (una suspensión se reactiva). Una membresía
/// <see cref="MembershipState.Removed"/> o <see cref="MembershipState.Expired"/> no cuenta,
/// aunque ninguna de las dos es terminal: las dos se pueden volver a invitar
/// (<c>Membership.Reinvite</c>). Si son las únicas que quedan, el usuario es un huérfano y se
/// puede borrar. Qué hace después una invitación nueva depende de si alcanzó a borrarse: si el
/// usuario sigue en Identity, reutiliza esa misma fila; si ya no está, crea un usuario nuevo y,
/// con él, una membresía nueva.
/// </summary>
public sealed class MembershipUserReferenceProbe(IMembershipRepository membershipRepository)
    : IUserReferenceProbe
{
    public string Source => "tenancy";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await membershipRepository.ListByUserAsync(userId, cancellationToken);
        return memberships.Any(membership => membership.State is
            MembershipState.Invited or
            MembershipState.Active or
            MembershipState.Suspended);
    }
}

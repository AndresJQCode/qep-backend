using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Tenancy retiene a un usuario mientras conserve una membresía que le da o le promete acceso
/// (<see cref="Membership.GrantsOrPromisesAccess"/>):
/// <see cref="MembershipState.Invited"/>, <see cref="MembershipState.Active"/> o
/// <see cref="MembershipState.Suspended"/> (una suspensión se reactiva). Una membresía
/// <see cref="MembershipState.Removed"/> o <see cref="MembershipState.Expired"/> no cuenta,
/// aunque ninguna de las dos es terminal: las dos se pueden volver a invitar
/// (<c>Membership.Reinvite</c>). Si son las únicas que quedan, el usuario es un huérfano y se
/// puede borrar, y antes de borrarlo <see cref="MembershipUserReferencePurger"/> borra esas
/// filas, con lo que su código de asesor queda libre (spec 2026-10-02). Qué hace después una
/// invitación nueva depende de si alcanzó a borrarse: si el usuario sigue en Identity, reutiliza
/// esa misma fila; si ya no está, crea un usuario nuevo y, con él, una membresía nueva, que puede
/// volver a tomar el código de la vieja.
/// </summary>
public sealed class MembershipUserReferenceProbe(IMembershipRepository membershipRepository)
    : IUserReferenceProbe
{
    public string Source => "tenancy";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await membershipRepository.ListByUserAsync(userId, cancellationToken);
        // La misma frontera que Membership.EnsurePurgeable: ver Membership.GrantsOrPromisesAccess.
        return memberships.Any(membership => membership.GrantsOrPromisesAccess);
    }
}

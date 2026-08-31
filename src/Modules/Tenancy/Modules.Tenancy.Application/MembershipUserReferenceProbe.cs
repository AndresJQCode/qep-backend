using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Tenancy retiene a un usuario mientras conserve una membresía que todavía puede volver a
/// usarse: <see cref="MembershipState.Invited"/>, <see cref="MembershipState.Active"/> o
/// <see cref="MembershipState.Suspended"/> (una suspensión se reactiva). Una membresía
/// <see cref="MembershipState.Removed"/> o <see cref="MembershipState.Expired"/> es terminal
/// —<c>Membership.Remove</c> rechaza tocarla— y no cuenta: si son las únicas que quedan, el
/// usuario es un huérfano y una invitación futura crea uno nuevo.
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

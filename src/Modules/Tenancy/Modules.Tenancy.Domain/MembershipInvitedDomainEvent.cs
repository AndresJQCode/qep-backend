using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

/// <summary>
/// <paramref name="Token"/> es el token de invitación en claro, y este evento es el único
/// lugar por donde viaja: el outbox lo lleva hasta el worker de Notifications, que arma el
/// link del email. En la fila de la membresía queda sólo el hash
/// (<see cref="Membership.InvitationTokenHash"/>).
/// </summary>
public sealed record MembershipInvitedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    MembershipId MembershipId,
    TenantId TenantId,
    Guid UserId,
    DateTimeOffset ExpiresAt,
    string Token) : IDomainEvent;

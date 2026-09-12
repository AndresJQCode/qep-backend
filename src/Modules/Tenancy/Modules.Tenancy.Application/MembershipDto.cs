using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <param name="DisplayName">
/// Nulo cuando la membresía es anterior al nombre o es la del owner: el reinvite no-op devuelve
/// la fila existente tal como está (spec 2026-09-11, D5).
/// </param>
public sealed record MembershipDto(
    MembershipId Id,
    Guid UserId,
    string? DisplayName,
    TenantId TenantId,
    MembershipState State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version);

public static class MembershipMappings
{
    public static MembershipDto ToDto(this Membership membership) =>
        new(
            membership.Id,
            membership.UserId,
            membership.DisplayName,
            membership.TenantId,
            membership.State,
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);
}

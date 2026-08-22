using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record MembershipDto(
    MembershipId Id,
    Guid UserId,
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
            membership.TenantId,
            membership.State,
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);
}

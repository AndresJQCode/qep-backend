using BuildingBlocks.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record MembershipListItemDto(
    MembershipId Id,
    Guid UserId,
    string? Email,
    TenantId TenantId,
    MembershipState State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version);

public static class MembershipListItemMappings
{
    public static MembershipListItemDto ToListItemDto(this Membership membership, string? email) =>
        new(
            membership.Id,
            membership.UserId,
            email,
            membership.TenantId,
            membership.State,
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);
}

public sealed record ListMembershipsQuery(TenantId TenantId) : IQuery<IReadOnlyList<MembershipListItemDto>>;

public sealed class ListMembershipsHandler(
    IMembershipRepository membershipRepository,
    IUserDirectory userDirectory,
    IExecutionContext executionContext)
    : IQueryHandler<ListMembershipsQuery, IReadOnlyList<MembershipListItemDto>>
{
    public async Task<IReadOnlyList<MembershipListItemDto>> HandleAsync(
        ListMembershipsQuery query,
        CancellationToken cancellationToken)
    {
        EnsureAuthorized(query.TenantId);

        var memberships = await membershipRepository.ListByTenantAsync(
            query.TenantId,
            cancellationToken);

        var items = new List<MembershipListItemDto>(memberships.Count);
        // Una búsqueda por membresía: IUserDirectory sólo expone resolución por id único
        // (v1). Aceptable para la poca cantidad de miembros que tiene un tenant hoy.
        foreach (var membership in memberships)
        {
            var email = await userDirectory.GetEmailAsync(membership.UserId, cancellationToken);
            items.Add(membership.ToListItemDto(email));
        }

        return items;
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipRead))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read memberships for this tenant.");
        }
    }
}

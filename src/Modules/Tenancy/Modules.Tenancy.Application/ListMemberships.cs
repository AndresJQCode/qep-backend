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

/// <summary>Filtro opcional por rol (ej. "advisor") — la lista de asesores para el
/// selector de cotizaciones se resuelve acá, en el servidor, no descartando filas del
/// lado del cliente. Sin filtro, se listan todos los roles, como antes.</summary>
public sealed record ListMembershipsQuery(TenantId TenantId, string? Role = null)
    : IQuery<IReadOnlyList<MembershipListItemDto>>;

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

        // Filtrado en memoria, no en el repositorio: mismo criterio que ya documenta este
        // handler para la resolución de email — la cantidad de miembros de un tenant es
        // chica hoy, no justifica un método de repositorio nuevo.
        var filtered = query.Role is null
            ? memberships
            : memberships.Where(membership => membership.Roles.Contains(query.Role)).ToList();

        var items = new List<MembershipListItemDto>(filtered.Count);
        // Una búsqueda por membresía: IUserDirectory sólo expone resolución por id único
        // (v1). Aceptable para la poca cantidad de miembros que tiene un tenant hoy.
        foreach (var membership in filtered)
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

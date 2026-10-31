using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec cobros-recurrentes §3.3: los miembros que ServiceContracts puede nombrar como encargados. Membresía del tenant (Tenancy) +
/// correo (Identity) + roles → permiso (Authorization, por <see cref="IRolePermissionChecker"/>), sin caché: mismo patrón que
/// <see cref="MessagingAssignees"/>.
///
/// Elegible como encargado es quien tiene la membresía activa y <c>billing.service.manage</c> (owner, 2026-10-29), no
/// cualquier miembro: el encargado recibe el email diario y es quien cobra.
/// </summary>
internal sealed class ServiceContractsMemberDirectory(
    TenancyDbContext tenancy,
    IUserDirectory users,
    IRolePermissionChecker permissions) : IServiceContractsMemberDirectory
{
    // El mismo rótulo de MembershipResponse.DisplayName nulo en el panel de Personas (MembershipEndpoints.cs:336).
    private const string NoName = "Sin nombre";

    public async Task<IReadOnlyList<ServiceContractsMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return [];
        }

        var ids = memberIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var tenant = new TenantId(tenantId);
        var memberships = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && ids.Contains(membership.Id))
            .ToListAsync(cancellationToken);

        var result = new List<ServiceContractsMemberRef>(memberships.Count);
        foreach (var membership in memberships)
        {
            result.Add(await ToRefAsync(tenant, membership, cancellationToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<ServiceContractsMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var active = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && membership.State == MembershipState.Active)
            .ToListAsync(cancellationToken);

        var managers = new List<ServiceContractsMemberRef>();
        foreach (var membership in active)
        {
            var member = await ToRefAsync(tenant, membership, cancellationToken);
            if (member.CanManage)
            {
                managers.Add(member);
            }
        }

        // Orden estable: es la lista de GET /assignees y el reparto de destinatarios del email de reserva.
        return managers
            .OrderBy(member => member.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .ToArray();
    }

    public async Task<ServiceContractsMemberRef?> FindManagerByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken)
    {
        // Sobre la misma lista de GET /assignees: un encargado de la importación es exactamente uno de los que el formulario ofrece.
        var wanted = email.Trim();
        var managers = await ListManagersAsync(tenantId, cancellationToken);
        return managers.FirstOrDefault(member => string.Equals(member.Email, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ServiceContractsMemberRef> ToRefAsync(TenantId tenant, Membership membership, CancellationToken cancellationToken)
    {
        var isActive = membership.State == MembershipState.Active;
        // Sólo se consulta a Authorization por quien está activo: una membresía suspendida o quitada no gestiona nada,
        // aunque su rol lo conceda.
        var canManage = isActive
            && await permissions.AnyGrantsAsync(tenant, membership.Roles, ServiceContractsPermissions.Manage, cancellationToken);
        var displayName = string.IsNullOrWhiteSpace(membership.DisplayName) ? NoName : membership.DisplayName;
        var email = await users.GetEmailAsync(membership.UserId, cancellationToken);
        return new ServiceContractsMemberRef(membership.Id.Value, displayName, email, isActive, canManage);
    }
}

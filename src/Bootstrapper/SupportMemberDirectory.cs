using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Los miembros que Soporte puede nombrar como responsables (copia de <see cref="LoansMemberDirectory"/>). Membresía del tenant
/// (Tenancy) + roles → permiso (Authorization, por <see cref="IRolePermissionChecker"/>), sin caché.
///
/// Elegible como responsable es quien tiene la membresía activa y <c>support.ticket.manage</c>, no cualquier miembro: el
/// responsable es quien gestiona el ticket.
/// </summary>
internal sealed class SupportMemberDirectory(
    TenancyDbContext tenancy,
    IRolePermissionChecker permissions) : ISupportMemberDirectory
{
    // El mismo rótulo de MembershipResponse.DisplayName nulo en el panel de Personas (MembershipEndpoints.cs:336).
    private const string NoName = "Sin nombre";

    public async Task<IReadOnlyList<SupportMemberRef>> FindAsync(
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

        var result = new List<SupportMemberRef>(memberships.Count);
        foreach (var membership in memberships)
        {
            result.Add(await ToRefAsync(tenant, membership, cancellationToken));
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = memberIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var tenant = new TenantId(tenantId);
        var memberships = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && ids.Contains(membership.Id))
            .Select(membership => new { membership.Id, membership.DisplayName })
            .ToListAsync(cancellationToken);
        return memberships.ToDictionary(
            membership => membership.Id.Value,
            membership => DisplayNameOf(membership.DisplayName));
    }

    public async Task<IReadOnlyList<SupportMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var active = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && membership.State == MembershipState.Active)
            .ToListAsync(cancellationToken);

        var managers = new List<SupportMemberRef>();
        foreach (var membership in active)
        {
            var member = await ToRefAsync(tenant, membership, cancellationToken);
            if (member.CanManage)
            {
                managers.Add(member);
            }
        }

        // Sin ordenar: el orden de GET /assignees lo da ListSupportAssigneesHandler, una sola vez (revisión final, B3).
        return managers;
    }

    public async Task<IReadOnlyList<SupportMemberRef>> ListTaskMembersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var active = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && membership.State == MembershipState.Active)
            .ToListAsync(cancellationToken);

        var members = new List<SupportMemberRef>();
        foreach (var membership in active)
        {
            var member = await ToRefAsync(tenant, membership, cancellationToken);
            if (member.CanManage || member.CanUseTasks)
            {
                members.Add(member);
            }
        }

        // Sin ordenar, como ListManagersAsync: el orden lo da ListTaskAssigneesHandler.
        return members;
    }

    private async Task<SupportMemberRef> ToRefAsync(TenantId tenant, Membership membership, CancellationToken cancellationToken)
    {
        var isActive = membership.State == MembershipState.Active;
        // Sólo se consulta a Authorization por quien está activo: una membresía suspendida o quitada no gestiona nada,
        // aunque su rol lo conceda.
        var canManage = isActive
            && await permissions.AnyGrantsAsync(tenant, membership.Roles, SupportPermissions.Manage, cancellationToken);
        var canUseTasks = isActive
            && await permissions.AnyGrantsAsync(tenant, membership.Roles, SupportPermissions.TaskUse, cancellationToken);
        return new SupportMemberRef(membership.Id.Value, DisplayNameOf(membership.DisplayName), isActive, canManage, canUseTasks);
    }

    private static string DisplayNameOf(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? NoName : displayName;
}

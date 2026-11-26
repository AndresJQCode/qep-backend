using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Wms.Application;

namespace Bootstrapper;

/// <summary>
/// Tenancy → Wms (tareas, spec 2026-11-26): el nombre visible de las membresías y cuáles pueden hacer una tarea. Los nombres como
/// <see cref="PurchasesMemberDirectory"/> (en cualquier estado) y el permiso como <see cref="SupportMemberPermissions"/> (membresía
/// activa + roles → permiso por <see cref="IRolePermissionChecker"/>, sin caché).
/// </summary>
internal sealed class WmsMemberDirectory(TenancyDbContext tenancy, IRolePermissionChecker permissions) : IWmsMemberDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        if (memberIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = memberIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var tenant = new TenantId(tenantId);
        var rows = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && ids.Contains(membership.Id))
            .Select(membership => new { membership.Id, membership.DisplayName })
            .ToListAsync(ct);

        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.DisplayName))
            .ToDictionary(row => row.Id.Value, row => row.DisplayName!);
    }

    public async Task<bool> CanPerformAsync(Guid tenantId, Guid memberId, string permission, CancellationToken ct)
    {
        var tenant = new TenantId(tenantId);
        var id = new MembershipId(memberId);
        var membership = await tenancy.Memberships.AsNoTracking()
            .Where(candidate => candidate.TenantId == tenant && candidate.Id == id && candidate.State == MembershipState.Active)
            .SingleOrDefaultAsync(ct);
        return membership is not null && await permissions.AnyGrantsAsync(tenant, membership.Roles, permission, ct);
    }

    public async Task<IReadOnlyList<Guid>> ListWithPermissionAsync(Guid tenantId, string permission, CancellationToken ct)
    {
        var tenant = new TenantId(tenantId);
        var active = await tenancy.Memberships.AsNoTracking()
            .Where(candidate => candidate.TenantId == tenant && candidate.State == MembershipState.Active)
            .ToListAsync(ct);

        // Las personas de una bodega comparten pocas combinaciones de roles: cada una se consulta una vez.
        var granted = new Dictionary<string, bool>(StringComparer.Ordinal);
        var members = new List<Guid>();
        foreach (var membership in active)
        {
            var roles = string.Join('\n', membership.Roles.Order(StringComparer.Ordinal));
            if (!granted.TryGetValue(roles, out var grants))
            {
                grants = await permissions.AnyGrantsAsync(tenant, membership.Roles, permission, ct);
                granted[roles] = grants;
            }

            if (grants)
            {
                members.Add(membership.Id.Value);
            }
        }

        return members;
    }
}

using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Tenancy → Wholesale: el padrón del mayorista (forma de <see cref="PurchasesMemberDirectory"/>). Asesor puede ser cualquier
/// membresía activa, sin filtro de rol (D-15). Los nombres se leen en cualquier estado, para que un asesor ido siga nombrado;
/// una membresía sin nombre no viene y el composer pone «Sin nombre».
/// </summary>
internal sealed class WholesaleMemberDirectory(TenancyDbContext tenancy) : IWholesaleMemberDirectory
{
    // El mismo rótulo de MembershipResponse.DisplayName nulo en el panel de Personas.
    private const string NoName = "Sin nombre";

    public async Task<IReadOnlyList<WholesaleMember>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var rows = await tenancy.Memberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenant && membership.State == MembershipState.Active)
            .Select(membership => new { membership.Id, membership.DisplayName })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new WholesaleMember(row.Id.Value, string.IsNullOrWhiteSpace(row.DisplayName) ? NoName : row.DisplayName))
            .OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
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
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.DisplayName))
            .ToDictionary(row => row.Id.Value, row => row.DisplayName!);
    }
}

/// <summary>Tenancy → Wholesale: el nombre vigente de los tenants distribuidores, en una consulta por página.</summary>
internal sealed class WholesaleTenantDirectory(TenancyDbContext tenancy) : IWholesaleTenantDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        if (tenantIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = tenantIds.Distinct().Select(id => new TenantId(id)).ToArray();
        var rows = await tenancy.Tenants.AsNoTracking()
            .Where(tenant => ids.Contains(tenant.Id))
            .Select(tenant => new { tenant.Id, tenant.DisplayName })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Id.Value, row => row.DisplayName);
    }
}

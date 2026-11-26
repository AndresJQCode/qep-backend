using Microsoft.EntityFrameworkCore;
using Modules.Purchases.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Tenancy → Purchases: el nombre visible de las membresías que crearon una orden o dejaron un paso en su historial. En cualquier
/// estado, porque la orden sigue nombrando a quien la hizo aunque después lo hayan quitado. Una membresía sin nombre no viene: el
/// composer pone «Sin nombre».
/// </summary>
internal sealed class PurchasesMemberDirectory(TenancyDbContext tenancy) : IPurchasesMemberDirectory
{
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

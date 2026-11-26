using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Wholesale.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-22 (D-11): Catalog no ve Mayorista, así que cuántos distribuidores usan cada lista lo cuenta este adaptador, en
/// cualquier estado del distribuidor (es lo que impide borrarla). Lee <c>wholesale.distributors</c> por su <c>DbContext</c>.
/// </summary>
internal sealed class CatalogPriceListUsage(WholesaleDbContext wholesale) : IPriceListUsage
{
    public async Task<IReadOnlyDictionary<Guid, int>> CountDistributorsByPriceListAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var counts = await wholesale.Distributors.AsNoTracking()
            .Where(distributor => distributor.TenantId == tenantId)
            .GroupBy(distributor => distributor.PriceListId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(entry => entry.Key.Value, entry => entry.Count);
    }
}

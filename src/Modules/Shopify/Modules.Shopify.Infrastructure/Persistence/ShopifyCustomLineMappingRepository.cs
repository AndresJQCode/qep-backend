using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>Spec 2026-11-13: <see cref="IShopifyCustomLineMappingRepository"/> sobre <see cref="ShopifyDbContext"/>. El 409 por
/// título repetido no se detecta acá con una consulta previa: lo da el índice único al guardar (<c>ShopifyUnitOfWork</c>).</summary>
internal sealed class ShopifyCustomLineMappingRepository(ShopifyDbContext dbContext) : IShopifyCustomLineMappingRepository
{
    public async Task<IReadOnlyDictionary<string, Guid>> FindVariantIdsByTitlesAsync(
        Guid tenantId, Guid connectionId, IReadOnlyCollection<string> titles, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (titles.Count == 0)
        {
            return map;
        }

        var wanted = titles.ToArray();
        var rows = await dbContext.CustomLineMappings
            .AsNoTracking()
            .Where(mapping => mapping.TenantId == tenantId && mapping.ConnectionId == connectionId && wanted.Contains(mapping.Title))
            .Select(mapping => new { mapping.Title, mapping.VariantId })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            map[row.Title] = row.VariantId;
        }

        return map;
    }

    public async Task<IReadOnlyList<ShopifyCustomLineMapping>> ListAsync(
        Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await dbContext.CustomLineMappings
            .AsNoTracking()
            .Where(mapping => mapping.TenantId == tenantId && mapping.ConnectionId == connectionId)
            .ToListAsync(cancellationToken);

    public Task<ShopifyCustomLineMapping?> FindAsync(Guid tenantId, Guid connectionId, Guid id, CancellationToken cancellationToken) =>
        dbContext.CustomLineMappings.SingleOrDefaultAsync(
            mapping => mapping.Id == id && mapping.TenantId == tenantId && mapping.ConnectionId == connectionId,
            cancellationToken);

    public void Add(ShopifyCustomLineMapping mapping) => dbContext.CustomLineMappings.Add(mapping);

    public void Remove(ShopifyCustomLineMapping mapping) => dbContext.CustomLineMappings.Remove(mapping);
}

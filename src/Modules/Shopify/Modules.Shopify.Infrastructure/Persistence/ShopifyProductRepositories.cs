using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>Spec 2026-11-17 §3: <see cref="IShopifyProductLinkRepository"/> sobre <see cref="ShopifyDbContext"/>. Las carreras las
/// cierran los índices únicos al guardar (<c>ShopifyUnitOfWork</c>), no una consulta previa.</summary>
internal sealed class ShopifyProductLinkRepository(ShopifyDbContext dbContext) : IShopifyProductLinkRepository
{
    public async Task<IReadOnlyList<ShopifyProductLink>> FindByShopifyProductAsync(
        Guid connectionId, long shopifyProductId, CancellationToken cancellationToken) =>
        await dbContext.ProductLinks
            .Where(link => link.ConnectionId == connectionId && link.ShopifyProductId == shopifyProductId)
            .OrderBy(link => link.CreatedAt)
            .ThenBy(link => link.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<long, ShopifyProductLink>> FindByShopifyVariantsAsync(
        Guid tenantId, Guid connectionId, IReadOnlyCollection<long> shopifyVariantIds, CancellationToken cancellationToken)
    {
        var wanted = shopifyVariantIds.Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return new Dictionary<long, ShopifyProductLink>();
        }

        var rows = await dbContext.ProductLinks
            .AsNoTracking()
            .Where(link => link.TenantId == tenantId && link.ConnectionId == connectionId && wanted.Contains(link.ShopifyVariantId))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(link => link.ShopifyVariantId);
    }

    public async Task<IReadOnlyList<ShopifyProductLink>> FindByProductAsync(
        Guid tenantId, Guid connectionId, Guid productId, CancellationToken cancellationToken) =>
        await dbContext.ProductLinks
            .Where(link => link.TenantId == tenantId && link.ConnectionId == connectionId && link.ProductId == productId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ShopifyProductLink>> FindByProductInTenantAsync(
        Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        await dbContext.ProductLinks
            .Where(link => link.TenantId == tenantId && link.ProductId == productId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ShopifyProductLink>> FindByVariantsAsync(
        Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        var wanted = variantIds.Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return [];
        }

        return await dbContext.ProductLinks
            .Where(link => link.ConnectionId == connectionId && wanted.Contains(link.VariantId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ListImportedProductIdsAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var ids = await dbContext.ProductLinks
            .AsNoTracking()
            .Where(link => link.ConnectionId == connectionId && link.Origin == ShopifyProductLinkOrigin.Imported)
            .Select(link => link.ProductId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public async Task<IReadOnlySet<long>> ListLinkedShopifyVariantIdsAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var ids = await dbContext.ProductLinks
            .AsNoTracking()
            .Where(link => link.ConnectionId == connectionId)
            .Select(link => link.ShopifyVariantId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public void Add(ShopifyProductLink link) => dbContext.ProductLinks.Add(link);

    public void Remove(ShopifyProductLink link) => dbContext.ProductLinks.Remove(link);
}

/// <summary>Spec 2026-11-17 §5.2: <see cref="IShopifyProductImportRepository"/> sobre <see cref="ShopifyDbContext"/>. El 409 de una
/// segunda importación viva lo da <c>IX_product_imports_connection_live</c> al guardar.</summary>
internal sealed class ShopifyProductImportRepository(ShopifyDbContext dbContext) : IShopifyProductImportRepository
{
    public Task<ShopifyProductImport?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.ProductImports.SingleOrDefaultAsync(import => import.Id == id && import.TenantId == tenantId, cancellationToken);

    public Task<ShopifyProductImport?> FindLatestAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.ProductImports
            .AsNoTracking()
            .Where(import => import.TenantId == tenantId && import.ConnectionId == connectionId)
            .OrderByDescending(import => import.CreatedAt)
            .ThenByDescending(import => import.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(ShopifyProductImport import) => dbContext.ProductImports.Add(import);

    public void AddIssue(ShopifyProductImportIssue issue) => dbContext.ProductImportIssues.Add(issue);

    public async Task<IReadOnlyList<ShopifyProductImportIssue>> ListIssuesAsync(Guid importId, int limit, CancellationToken cancellationToken) =>
        await dbContext.ProductImportIssues
            .AsNoTracking()
            .Where(issue => issue.ImportId == importId)
            .OrderBy(issue => issue.CreatedAt)
            .ThenBy(issue => issue.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
}

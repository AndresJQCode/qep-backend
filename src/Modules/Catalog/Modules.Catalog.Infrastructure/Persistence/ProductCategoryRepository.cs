using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class ProductCategoryRepository(CatalogDbContext dbContext) : IProductCategoryRepository
{
    public async Task<IReadOnlyList<ProductCategoryListItem>> ListAsync(
        Guid tenantId,
        bool onlyActiveProducts,
        CancellationToken cancellationToken)
    {
        var categories = await ListTreeAsync(tenantId, cancellationToken);

        // Una segunda consulta agrupada y no un Count por categoría: nunca N+1.
        var products = dbContext.Products
            .AsNoTracking()
            .Where(product => product.TenantId == tenantId && product.CategoryId != null);
        if (onlyActiveProducts)
        {
            products = products.Where(product => product.IsActive);
        }

        var counts = (await products
                .GroupBy(product => product.CategoryId)
                .Select(group => new { CategoryId = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .Where(row => row.CategoryId.HasValue)
            .ToDictionary(row => row.CategoryId!.Value, row => row.Count);

        return categories
            .Select(category => new ProductCategoryListItem(
                category,
                counts.GetValueOrDefault(category.Id)))
            .ToList();
    }

    public async Task<IReadOnlyList<ProductCategory>> ListTreeAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.ProductCategories
            .AsNoTracking()
            .Where(category => category.TenantId == tenantId)
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<ProductCategoryId>> ListIdsWithDescendantsAsync(
        Guid tenantId,
        ProductCategoryId categoryId,
        CancellationToken cancellationToken) =>
        ProductCategoryTree.WithDescendants(
            await ListTreeAsync(tenantId, cancellationToken),
            categoryId);

    public Task<ProductCategory?> FindAsync(
        Guid tenantId,
        ProductCategoryId categoryId,
        CancellationToken cancellationToken) =>
        dbContext.ProductCategories
            .AsNoTracking()
            .SingleOrDefaultAsync(
                category => category.TenantId == tenantId && category.Id == categoryId,
                cancellationToken);

    public void Add(ProductCategory category) => dbContext.ProductCategories.Add(category);
}

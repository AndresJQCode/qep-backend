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

    public async Task<IReadOnlyList<ProductCategoryNode>> ListCategoriesWithParentsAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.ProductCategories
            .AsNoTracking()
            .Where(category => category.TenantId == tenantId)
            .OrderBy(category => category.Name)
            .Select(category => new ProductCategoryNode(category.Id, category.Name, category.ParentId))
            .ToListAsync(cancellationToken);

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

    public async Task<IReadOnlyList<ProductCategory>> ListForUpdateAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.ProductCategories
            .Where(category => category.TenantId == tenantId)
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken);

    public Task<int> CountProductsAsync(
        Guid tenantId,
        ProductCategoryId categoryId,
        CancellationToken cancellationToken) =>
        dbContext.Products
            .AsNoTracking()
            .CountAsync(
                product => product.TenantId == tenantId && product.CategoryId == categoryId,
                cancellationToken);

    public void Add(ProductCategory category) => dbContext.ProductCategories.Add(category);

    // DeleteBehavior.Restrict nulls the parent_id of every child this context tracks as soon as the
    // parent is marked Deleted, so the RESTRICT FK never sees them and they silently become roots. A
    // tracked child is refused here, before EF touches it; an untracked one (the race after the
    // load) still reaches the FK, which CatalogUnitOfWork translates to the same code. A child
    // deleted in the same unit of work is not in the way.
    public void Remove(ProductCategory category)
    {
        var hasTrackedChild = dbContext.ChangeTracker.Entries<ProductCategory>().Any(entry =>
            entry.State != EntityState.Deleted && entry.Entity.ParentId == category.Id);
        if (hasTrackedChild)
        {
            throw CategoryInUse.HasChildren();
        }

        dbContext.ProductCategories.Remove(category);
    }
}

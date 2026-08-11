using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class ProductRepository(CatalogDbContext dbContext) : IProductRepository
{
    public async Task<IReadOnlyList<Product>> SearchAsync(
        Guid tenantId,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Products
            .AsNoTracking()
            .Where(product => product.TenantId == tenantId);

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // ILike is the Npgsql case-insensitive match; the wildcards are ours and the
            // term travels as a parameter.
            var pattern = $"%{term}%";
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, pattern) ||
                EF.Functions.ILike(product.Code, pattern));
        }

        return await query
            .OrderBy(product => product.Name)
            .ToListAsync(cancellationToken);
    }

    // Tracked on purpose, unlike SearchAsync: the callers of this one mutate the aggregate
    // and rely on the unit of work to persist it.
    public Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
        CancellationToken cancellationToken) =>
        dbContext.Products.SingleOrDefaultAsync(
            product => product.TenantId == tenantId && product.Id == productId,
            cancellationToken);

    public void Add(Product product) => dbContext.Products.Add(product);
}

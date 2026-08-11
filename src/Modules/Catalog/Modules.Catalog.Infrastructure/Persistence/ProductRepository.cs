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
            // ILike es la coincidencia case-insensitive de Npgsql; los comodines son nuestros y el
            // término viaja como parámetro.
            var pattern = $"%{term}%";
            query = query.Where(product =>
                EF.Functions.ILike(product.Name, pattern) ||
                EF.Functions.ILike(product.Code, pattern));
        }

        return await query
            .OrderBy(product => product.Name)
            .ToListAsync(cancellationToken);
    }

    // Con tracking a propósito, a diferencia de SearchAsync: los llamadores de éste mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    public Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
        CancellationToken cancellationToken) =>
        dbContext.Products.SingleOrDefaultAsync(
            product => product.TenantId == tenantId && product.Id == productId,
            cancellationToken);

    public void Add(Product product) => dbContext.Products.Add(product);
}

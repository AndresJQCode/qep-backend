using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// Every method takes tenantId first: the tenant filter is part of the query, never an
// optional argument a caller can forget.
public interface IProductRepository
{
    Task<IReadOnlyList<Product>> SearchAsync(
        Guid tenantId,
        string? search,
        CancellationToken cancellationToken);

    Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
        CancellationToken cancellationToken);

    void Add(Product product);
}

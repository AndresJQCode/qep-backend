using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// Todo método recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca
// un argumento opcional que el llamador se pueda olvidar.
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

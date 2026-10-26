using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Las categorías en memoria, cada una con su conteo **directo** de productos activos, como lo
/// entrega el repositorio real con <c>onlyActiveProducts: true</c>. Filtra por tenant y ordena por
/// nombre como él; el árbol sale de <see cref="ProductCategoryTree"/>, la misma regla que usa la consulta.
/// </summary>
internal sealed class FakeProductCategoryRepository(params (ProductCategory Category, int ActiveCount)[] rows)
    : IProductCategoryRepository
{
    public Task<IReadOnlyList<ProductCategoryListItem>> ListAsync(
        Guid tenantId, bool onlyActiveProducts, CancellationToken cancellationToken) =>
        onlyActiveProducts
            ? Task.FromResult<IReadOnlyList<ProductCategoryListItem>>(rows
                .Where(row => row.Category.TenantId == tenantId)
                .OrderBy(row => row.Category.Name, StringComparer.Ordinal)
                .Select(row => new ProductCategoryListItem(row.Category, row.ActiveCount))
                .ToList())
            // El POS sólo cuenta activos: si alguien pide todos, el doble no sabe qué contestar.
            : throw new NotSupportedException();

    public Task<IReadOnlyList<ProductCategory>> ListTreeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProductCategory>>(OfTenant(tenantId));

    public Task<IReadOnlyCollection<ProductCategoryId>> ListIdsWithDescendantsAsync(
        Guid tenantId, ProductCategoryId categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(ProductCategoryTree.WithDescendants(OfTenant(tenantId), categoryId));

    public Task<ProductCategory?> FindAsync(Guid tenantId, ProductCategoryId categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(OfTenant(tenantId).FirstOrDefault(category => category.Id == categoryId));

    public void Add(ProductCategory category) => throw new NotSupportedException();

    private List<ProductCategory> OfTenant(Guid tenantId) =>
        rows.Select(row => row.Category)
            .Where(category => category.TenantId == tenantId)
            .OrderBy(category => category.Name, StringComparer.Ordinal)
            .ToList();
}

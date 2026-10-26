using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

/// <summary>Una categoría con la cantidad de productos que la tienen directamente asignada.</summary>
public sealed record ProductCategoryListItem(ProductCategory Category, int ProductCount);

// Todo método recibe tenantId primero: el filtro de tenant es parte de la consulta.
public interface IProductCategoryRepository
{
    /// <summary>
    /// Todas las categorías del tenant ordenadas por nombre, con el conteo **directo** de
    /// productos (los de las descendientes no suman). Con <paramref name="onlyActiveProducts"/>
    /// sólo cuentan los productos activos.
    /// </summary>
    Task<IReadOnlyList<ProductCategoryListItem>> ListAsync(
        Guid tenantId,
        bool onlyActiveProducts,
        CancellationToken cancellationToken);

    /// <summary>La lista plana del tenant, sin conteos, ordenada por nombre.</summary>
    Task<IReadOnlyList<ProductCategory>> ListTreeAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    /// <summary>La categoría y todas sus descendientes; vacío si el id no es del tenant.</summary>
    Task<IReadOnlyCollection<ProductCategoryId>> ListIdsWithDescendantsAsync(
        Guid tenantId,
        ProductCategoryId categoryId,
        CancellationToken cancellationToken);

    // Sin rastreo: nadie muta una categoría.
    Task<ProductCategory?> FindAsync(
        Guid tenantId,
        ProductCategoryId categoryId,
        CancellationToken cancellationToken);

    void Add(ProductCategory category);
}

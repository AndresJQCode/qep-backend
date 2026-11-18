using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Wholesale. Las categorías salen del árbol del tenant (una lectura); la muestra de la vista previa, de la búsqueda
/// del listado con productos activos y las categorías expandidas a sus descendientes (D-9). De cada producto se toma la primera
/// variante vendible (producto y variante activos, <see cref="CatalogVariantReads.IsSellable"/>) con precio en la moneda del
/// mayorista; un producto sin ninguna no se ofrece (D-10), aunque cuenta en <c>TotalProducts</c>, que es el total de la búsqueda.
/// </summary>
internal sealed class WholesaleCatalogReader(
    IProductCategoryRepository categories,
    IProductRepository products,
    IOptionDefinitionRepository options)
    : IWholesaleCatalogReader
{
    public async Task<IReadOnlyDictionary<Guid, WholesaleCategoryRef>> FindCategoriesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, WholesaleCategoryRef>();
        }

        // La consulta va acotada al tenant: un id ajeno o borrado simplemente no aparece.
        var wanted = ids.ToHashSet();
        return (await categories.ListCategoriesWithParentsAsync(tenantId, cancellationToken))
            .Where(node => wanted.Contains(node.Id.Value))
            .ToDictionary(node => node.Id.Value, node => new WholesaleCategoryRef(node.Id.Value, node.Name));
    }

    public async Task<IReadOnlyList<WholesaleCategoryNode>> ListCategoriesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await categories.ListCategoriesWithParentsAsync(tenantId, cancellationToken))
            .Select(node => new WholesaleCategoryNode(node.Id.Value, node.Name, node.ParentId?.Value))
            .ToList();

    public async Task<(IReadOnlyList<WholesaleSampleVariant> Items, int TotalProducts)> SampleAsync(
        Guid tenantId,
        string currency,
        IReadOnlyCollection<Guid>? categoryIds,
        int take,
        CancellationToken cancellationToken)
    {
        HashSet<Guid>? expanded = null;
        if (categoryIds is not null)
        {
            var tree = await categories.ListTreeAsync(tenantId, cancellationToken);
            expanded = categoryIds
                .SelectMany(id => ProductCategoryTree.WithDescendants(tree, new ProductCategoryId(id)))
                .Select(id => id.Value)
                .ToHashSet();
            if (expanded.Count == 0)
            {
                return ([], 0);
            }
        }

        var (found, total) = await products.SearchAsync(
            tenantId, null, null, null, isActive: true, expanded, page: 1, pageSize: take, cancellationToken);
        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(options, tenantId, found, cancellationToken);

        var items = new List<WholesaleSampleVariant>(found.Count);
        foreach (var product in found.OrderBy(product => product.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // Variants ya viene por posición.
            var variant = product.Variants.FirstOrDefault(candidate =>
                CatalogVariantReads.IsSellable(product, candidate) && candidate.PriceIn(currency) is not null);
            if (variant is null)
            {
                continue;
            }

            items.Add(new WholesaleSampleVariant(
                variant.Id.Value,
                variant.Code,
                CatalogVariantReads.DisplayNameOf(product, variant, valueNames),
                variant.PriceIn(currency)!.Value));
        }

        return (items, total);
    }
}

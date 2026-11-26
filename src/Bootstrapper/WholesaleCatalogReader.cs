using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Wholesale. Las categorías salen del árbol del tenant (una lectura); la muestra de la vista previa, de la búsqueda
/// del listado con productos activos y las categorías expandidas a sus descendientes (D-9). De cada producto se toma la primera
/// variante vendible (producto y variante activos, <see cref="CatalogVariantReads.IsSellable"/>) con precio en la moneda del
/// mayorista; un producto sin ninguna no se ofrece (D-10), aunque cuenta en <c>TotalProducts</c>, que es el total de la búsqueda.
/// <para>Fase 3: <see cref="FindSellableAsync"/> y <see cref="SearchSellableAsync"/> dan las variantes vendibles con su tasa de
/// IVA (sin tasa o con una borrada, 0 %, como <see cref="PurchasesCatalogLookup"/>) y el camino de categorías de su producto
/// (la categoría y sus ancestros), para que Wholesale decida la visibilidad sin otra lectura.</para>
/// </summary>
internal sealed class WholesaleCatalogReader(
    IProductCategoryRepository categories,
    IProductRepository products,
    IOptionDefinitionRepository options,
    ITaxRateRepository taxRates,
    IProductImageLookup images)
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
        IReadOnlyCollection<Guid>? excludedCategoryIds,
        IReadOnlyCollection<Guid>? excludedProductIds,
        int take,
        CancellationToken cancellationToken)
    {
        var expanded = await ExpandAsync(tenantId, categoryIds, cancellationToken);
        if (expanded is { Count: 0 })
        {
            return ([], 0);
        }

        var hidden = await ExpandAsync(tenantId, excludedCategoryIds is { Count: > 0 } ? excludedCategoryIds : null, cancellationToken);
        var (found, total) = await products.SearchActiveFilteredAsync(
            tenantId, null, null, expanded, hidden, excludedProductIds, orderByName: false, page: 1, pageSize: take, cancellationToken);
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

    public async Task<IReadOnlyDictionary<Guid, WholesaleSellableVariant>> FindSellableAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, WholesaleSellableVariant>();
        }

        var wanted = variantIds.ToHashSet();
        // Acotada al tenant: la variante de otro tenant no aparece, aunque el comprador mande su id.
        var found = await products.FindVariantsByIdsAsync(
            tenantId, wanted.Select(id => new VariantId(id)).ToArray(), cancellationToken);
        var items = await ToSellableAsync(
            tenantId, currency, found, product => product.Variants.Where(variant => wanted.Contains(variant.Id.Value)),
            cancellationToken);
        return items.ToDictionary(item => item.VariantId);
    }

    public async Task<(IReadOnlyList<WholesaleSellableVariant> Items, int Total)> SearchSellableAsync(
        Guid tenantId,
        string currency,
        IReadOnlyCollection<Guid>? categoryIds,
        IReadOnlyCollection<Guid>? excludedCategoryIds,
        IReadOnlyCollection<Guid>? excludedProductIds,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var expanded = await ExpandAsync(tenantId, categoryIds, cancellationToken);
        if (expanded is { Count: 0 })
        {
            return ([], 0);
        }

        // Spec 2026-11-20: las exclusiones se filtran en la consulta (con sus descendientes), no sobre la página: así Total y
        // la paginación cuentan sólo lo que el distribuidor puede ver.
        var hidden = await ExpandAsync(tenantId, excludedCategoryIds is { Count: > 0 } ? excludedCategoryIds : null, cancellationToken);
        var (found, total) = await products.SearchActiveFilteredAsync(
            tenantId,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            null,
            expanded,
            hidden,
            excludedProductIds,
            orderByName: false,
            page,
            pageSize,
            cancellationToken);
        var ordered = found.OrderBy(product => product.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Variants ya viene por posición.
        var items = await ToSellableAsync(tenantId, currency, ordered, product => product.Variants, cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, WholesaleProductRef>> FindProductsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, WholesaleProductRef>();
        }

        // Acotada al tenant: un id ajeno o borrado simplemente no aparece.
        var found = await products.FindBasicsByIdsAsync(tenantId, ids.Select(id => new ProductId(id)).ToArray(), cancellationToken);
        return found.ToDictionary(
            product => product.Id.Value,
            product => new WholesaleProductRef(
                product.Id.Value, product.SingleVariantCode, product.Name, product.CategoryId?.Value, product.VariantCount, null));
    }

    public async Task<(IReadOnlyList<WholesaleProductRef> Items, int Total)> SearchProductsAsync(
        Guid tenantId,
        string currency,
        Guid? categoryId,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var expanded = await ExpandAsync(tenantId, categoryId is { } chosen ? [chosen] : null, cancellationToken);
        if (expanded is { Count: 0 })
        {
            return ([], 0);
        }

        // Los mismos productos que la lista puede mostrar: activos y con precio en la moneda; el Total los cuenta a ellos.
        var (found, total) = await products.SearchActiveFilteredAsync(
            tenantId,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            currency,
            expanded,
            null,
            null,
            orderByName: true,
            page,
            pageSize,
            cancellationToken);
        var files = await CatalogVariantReads.LoadImagesAsync(images, found.Select(product => product.ImageFileId), cancellationToken);
        // El orden por nombre lo pone la consulta (también con búsqueda): reordenar la página en memoria no lo arreglaría.
        var items = found
            .Select(product => RefOf(product, CatalogVariantReads.ImageUrlOf(product.ImageFileId, files, tenantId)))
            .ToList();
        return (items, total);
    }

    // El código sólo cuando lo identifica: con una sola variante, el suyo (ProductListCode, la regla de la grilla del catálogo).
    private static WholesaleProductRef RefOf(Product product, string? imageUrl) =>
        new(
            product.Id.Value,
            product.Variants.Count == 1 ? product.Variants[0].Code : null,
            product.Name,
            product.CategoryId?.Value,
            product.Variants.Count,
            imageUrl);

    private async Task<HashSet<Guid>?> ExpandAsync(
        Guid tenantId, IReadOnlyCollection<Guid>? categoryIds, CancellationToken cancellationToken)
    {
        if (categoryIds is null)
        {
            return null;
        }

        var tree = await categories.ListTreeAsync(tenantId, cancellationToken);
        return categoryIds
            .SelectMany(id => ProductCategoryTree.WithDescendants(tree, new ProductCategoryId(id)))
            .Select(id => id.Value)
            .ToHashSet();
    }

    private async Task<IReadOnlyList<WholesaleSellableVariant>> ToSellableAsync(
        Guid tenantId,
        string currency,
        IReadOnlyList<Product> found,
        Func<Product, IEnumerable<ProductVariant>> variantsOf,
        CancellationToken cancellationToken)
    {
        if (found.Count == 0)
        {
            return [];
        }

        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(options, tenantId, found, cancellationToken);
        var percentages = await CatalogVariantReads.LoadTaxPercentagesAsync(taxRates, tenantId, found, cancellationToken);
        var parents = (await categories.ListCategoriesWithParentsAsync(tenantId, cancellationToken))
            .ToDictionary(node => node.Id.Value, node => node.ParentId?.Value);

        // Primero las variantes que se ofrecen; luego las fotos de todas en UNA llamada (sin N+1).
        var sellable = new List<(Product Product, ProductVariant Variant, decimal Price)>();
        foreach (var product in found)
        {
            foreach (var variant in variantsOf(product))
            {
                if (CatalogVariantReads.IsSellable(product, variant) && variant.PriceIn(currency) is { } price)
                {
                    sellable.Add((product, variant, price));
                }
            }
        }

        // Las fotos son del mayorista: el re-chequeo de tenant de ImageUrlOf usa SU tenant, nunca el del comprador.
        var files = await CatalogVariantReads.LoadImagesAsync(
            images, sellable.Select(entry => (Guid?)CatalogVariantReads.ImageFileIdOf(entry.Product, entry.Variant)), cancellationToken);

        var items = new List<WholesaleSellableVariant>(sellable.Count);
        foreach (var (product, variant, price) in sellable)
        {
            var taxRate = product.TaxRateId is { } rateId && percentages.TryGetValue(rateId, out var percentage)
                ? percentage
                : 0m;
            items.Add(new WholesaleSellableVariant(
                variant.Id.Value,
                variant.Code,
                CatalogVariantReads.DisplayNameOf(product, variant, valueNames),
                price,
                taxRate,
                PathOf(product.CategoryId?.Value, parents),
                product.Id.Value,
                product.Name,
                CatalogVariantReads.ImageUrlOf(CatalogVariantReads.ImageFileIdOf(product, variant), files, tenantId)));
        }

        return items;
    }

    // El árbol tiene a lo sumo ProductCategory.MaxDepth niveles; el tope corta un ciclo que la base no debería tener.
    private static List<Guid> PathOf(Guid? categoryId, Dictionary<Guid, Guid?> parents)
    {
        var path = new List<Guid>();
        for (var current = categoryId;
             current is { } id && parents.TryGetValue(id, out var parent) && path.Count <= ProductCategory.MaxDepth;
             current = parent)
        {
            path.Add(id);
        }

        return path;
    }
}

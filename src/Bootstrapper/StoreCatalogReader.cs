using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Store. En la parte de ajustes sólo traduce: los nombres salen del árbol de categorías del tenant y las monedas con
/// precio, de <see cref="ICurrenciesInUse"/>, la fuente de <c>GET /catalog/currencies-in-use</c>.
/// <para>Las lecturas públicas siguen a <see cref="PosProductLookup"/>: nombre visible, opciones e imágenes de toda la página en una
/// sola llamada. El repositorio devuelve el agregado entero, así que acá se filtran las variantes por estado y por moneda, y cada
/// variante pedida se vuelve a comprobar: lo que el repositorio acota por producto, la tienda lo exige por variante.</para>
/// </summary>
internal sealed class StoreCatalogReader(
    IProductCategoryRepository categories,
    ICurrenciesInUse currenciesInUse,
    IProductRepository products,
    IOptionDefinitionRepository options,
    IProductImageLookup images)
    : IStoreCatalogReader
{
    public async Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        // La consulta ya va acotada al tenant: un id ajeno o borrado simplemente no aparece.
        var wanted = ids.ToHashSet();
        return (await categories.ListCategoriesWithParentsAsync(tenantId, cancellationToken))
            .Where(node => wanted.Contains(node.Id.Value))
            .ToDictionary(node => node.Id.Value, node => node.Name);
    }

    public async Task<IReadOnlyCollection<string>> CurrenciesInUseAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await currenciesInUse.GetAsync(tenantId, cancellationToken);

    public async Task<(IReadOnlyList<StoreProductCard> Items, int Total)> SearchAsync(
        Guid tenantId, string currency, string? q, Guid? categoryId, int page, int pageSize, CancellationToken cancellationToken)
    {
        // La categoría y todas sus descendientes. Una que no existe (o es de otro tenant) expande a nada: página vacía, nunca 404.
        Guid[]? categoryIds = null;
        if (categoryId is { } id)
        {
            var tree = (await CategoryTreeAsync(tenantId, cancellationToken))
                .Select(node => (node.Id, node.Name, node.ParentId))
                .ToList();
            var expanded = PublicCategoryTree.WithDescendants(tree, id);
            if (expanded.Count == 0)
            {
                return ([], 0);
            }

            categoryIds = expanded.ToArray();
        }

        var (found, total) = await products.SearchStoreVisibleAsync(
            tenantId, currency, string.IsNullOrWhiteSpace(q) ? null : q, categoryIds, page, pageSize, cancellationToken);
        var files = await CatalogVariantReads.LoadImagesAsync(
            images, found.Select(product => product.ImageFileId), cancellationToken);

        var cards = new List<StoreProductCard>(found.Count);
        foreach (var product in found)
        {
            var prices = SellablePrices(product, currency);
            if (prices.Count == 0)
            {
                // El predicado de la consulta lo garantiza; si el dato cambió entre la consulta y acá, no se pinta una tarjeta sin precio.
                continue;
            }

            cards.Add(new StoreProductCard(
                product.Id.Value,
                product.Name,
                product.CategoryId?.Value,
                CatalogVariantReads.ImageUrlOf(product.ImageFileId, files, tenantId),
                prices.Min(),
                prices.Max(),
                prices.Count > 1));
        }

        return (cards, total);
    }

    public async Task<IReadOnlyList<StoreCategoryCount>> CategoriesAsync(
        Guid tenantId, string currency, CancellationToken cancellationToken) =>
        (await products.CountStoreVisibleByCategoryAsync(tenantId, currency, cancellationToken))
            .Select(entry => new StoreCategoryCount(entry.Key.Value, entry.Value))
            .ToList();

    public async Task<IReadOnlyList<StoreCategoryNode>> CategoryTreeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await categories.ListCategoriesWithParentsAsync(tenantId, cancellationToken))
            .Select(node => new StoreCategoryNode(node.Id.Value, node.Name, node.ParentId?.Value))
            .ToList();

    public async Task<StoreProductView?> ProductAsync(
        Guid tenantId, Guid productId, string currency, bool includeScales, CancellationToken cancellationToken)
    {
        var product = await products.FindStoreVisibleAsync(tenantId, new ProductId(productId), currency, cancellationToken);
        if (product is null)
        {
            return null;
        }

        var sellable = product.Variants.Where(variant => IsSellableIn(product, variant, currency)).ToList();
        if (sellable.Count == 0)
        {
            return null;
        }

        // Las opciones del producto en una sola consulta: de ellas salen los nombres visibles y el selector de la ficha.
        var optionIds = product.OptionIds.Select(id => new OptionDefinitionId(id)).ToArray();
        var definitions = optionIds.Length == 0
            ? []
            : await options.ListByIdsAsync(tenantId, optionIds, cancellationToken);
        var valueNames = definitions
            .SelectMany(option => option.Values)
            .ToDictionary(value => value.Id, value => value.Value);
        var files = await CatalogVariantReads.LoadImagesAsync(
            images,
            sellable.Select(variant => CatalogVariantReads.ImageFileIdOf(product, variant))
                .Concat(product.Gallery.Select(image => (Guid?)image.FileId))
                .Append(product.ImageFileId),
            cancellationToken);

        var gallery = product.Gallery
            .Select(image => CatalogVariantReads.ImageUrlOf(image.FileId, files, tenantId))
            .OfType<string>()
            .ToList();
        var variants = sellable.Select(variant =>
        {
            var price = variant.PriceIn(currency)!.Value;
            return new StoreVariantView(
                variant.Id.Value,
                CatalogVariantReads.DisplayNameOf(product, variant, valueNames),
                variant.OptionValueIds,
                CatalogVariantReads.ImageUrlOf(CatalogVariantReads.ImageFileIdOf(product, variant), files, tenantId),
                price,
                includeScales ? variant.PackagingUnits : [],
                includeScales ? ScalesOf(variant, price) : []);
        }).ToList();

        return new StoreProductView(
            product.Id.Value,
            product.Name,
            product.Description,
            product.CategoryId?.Value,
            CatalogVariantReads.ImageUrlOf(product.ImageFileId, files, tenantId),
            gallery,
            OptionsOf(product, definitions, sellable),
            variants);
    }

    // Cada opción en el orden del producto, con sólo los valores que usa alguna variante vendible, en el orden de la opción: el
    // visitante no elige una talla que no puede comprar. Una opción que ya no está en la biblioteca no se ofrece.
    private static List<StoreOptionView> OptionsOf(
        Product product, IReadOnlyList<OptionDefinition> definitions, IReadOnlyList<ProductVariant> sellable)
    {
        var used = sellable.SelectMany(variant => variant.OptionValueIds).ToHashSet();
        var byId = definitions.ToDictionary(option => option.Id.Value);
        return product.OptionIds
            .Where(byId.ContainsKey)
            .Select(id => new StoreOptionView(
                byId[id].Name,
                byId[id].Values
                    .Where(value => used.Contains(value.Id))
                    .Select(value => new StoreOptionValueView(value.Id, value.Value))
                    .ToList()))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, StoreSellableVariant>> SellableVariantsAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, StoreSellableVariant>();
        }

        var wanted = variantIds.ToHashSet();
        var found = await products.FindStoreVisibleVariantsAsync(tenantId, currency, wanted, cancellationToken);

        // El repositorio devuelve productos enteros: acá se vuelve a exigir, variante por variante, activa y con precio en la moneda.
        var selected = CatalogVariantReads.Select(found, wanted)
            .Where(pair => IsSellableIn(pair.Product, pair.Variant, currency))
            .ToList();
        if (selected.Count == 0)
        {
            return new Dictionary<Guid, StoreSellableVariant>();
        }

        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(
            options, tenantId, selected.Select(pair => pair.Product).Distinct(), cancellationToken);
        var files = await CatalogVariantReads.LoadImagesAsync(
            images, selected.Select(pair => CatalogVariantReads.ImageFileIdOf(pair.Product, pair.Variant)), cancellationToken);

        return selected.ToDictionary(
            pair => pair.Variant.Id.Value,
            pair => new StoreSellableVariant(
                pair.Variant.Id.Value,
                pair.Product.Id.Value,
                CatalogVariantReads.DisplayNameOf(pair.Product, pair.Variant, valueNames),
                CatalogVariantReads.ImageUrlOf(CatalogVariantReads.ImageFileIdOf(pair.Product, pair.Variant), files, tenantId)));
    }

    private static bool IsSellableIn(Product product, ProductVariant variant, string currency) =>
        product.IsActive && product.VisibleInStore && variant.IsActive && variant.PriceIn(currency) is not null;

    private static List<decimal> SellablePrices(Product product, string currency) =>
        product.Variants
            .Where(variant => IsSellableIn(product, variant, currency))
            .Select(variant => variant.PriceIn(currency)!.Value)
            .ToList();

    // Una escala sin restricción es una copia incompleta que nadie puede cotizar: no se ofrece. El final lo deriva el servidor.
    private static List<StoreScaleView> ScalesOf(ProductVariant variant, decimal price) =>
        variant.PriceScales
            .Where(scale => scale.Restriction is not null)
            .OrderBy(scale => scale.FromUnit)
            .Select(scale => new StoreScaleView(
                scale.FromUnit,
                scale.ToUnit,
                scale.Discount,
                PriceScale.FinalFor(price, scale.Discount)!.Value,
                WireRestriction(scale.Restriction!.Value),
                scale.Multiple,
                scale.AllowGrouping))
            .ToList();

    // El mismo texto que el catálogo (ProductPricingMapping, interno a Catalog.Application): el contrato público habla como el del backoffice.
    private static string WireRestriction(PriceScaleRestriction restriction) => restriction switch
    {
        PriceScaleRestriction.Multiple => "multiple",
        PriceScaleRestriction.PackagingUnit => "packaging_unit",
        _ => throw new ArgumentOutOfRangeException(nameof(restriction), restriction, null),
    };
}

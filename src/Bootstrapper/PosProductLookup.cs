using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Pos.Application;

namespace Bootstrapper;

/// <summary>
/// Catalog → Pos, por **variante** (spec 2026-10-20 §5). Resuelve nombre visible, tasa e imagen
/// como los adaptadores de Quotations (<see cref="CatalogVariantReads"/>): opciones, tasas por id
/// distinto y las imágenes de toda la página en una sola llamada, no una por variante.
/// </summary>
internal sealed class PosProductLookup(
    IProductRepository products,
    IOptionDefinitionRepository options,
    ITaxRateRepository taxRates,
    IProductImageLookup images,
    IProductCategoryRepository categories)
    : IPosProductLookup
{
    public async Task<(IReadOnlyList<PosCatalogProductRef> Items, int Total)> SearchAsync(
        Guid tenantId, string? search, Guid? categoryId, int page, int pageSize, CancellationToken cancellationToken)
    {
        // Spec 2026-10-26 §3.4: la categoría y todas sus descendientes. Un id inexistente o de otro tenant
        // expande a nada, y eso es una página vacía (nunca un 404): se corta acá, sin consultar productos.
        Guid[]? categoryIds = null;
        if (categoryId is { } id)
        {
            var expanded = await categories.ListIdsWithDescendantsAsync(tenantId, new ProductCategoryId(id), cancellationToken);
            if (expanded.Count == 0)
            {
                return ([], 0);
            }

            categoryIds = expanded.Select(category => category.Value).ToArray();
        }

        var (items, total) = await products.SearchAsync(
            tenantId, string.IsNullOrWhiteSpace(search) ? null : search, null, null, true, categoryIds, page, pageSize, cancellationToken);

        // Sólo las variantes que se venden. El producto ya viene activo por el filtro de la consulta.
        var selected = items
            .SelectMany(product => product.Variants
                .Where(variant => CatalogVariantReads.IsSellable(product, variant))
                .Select(variant => (product, variant)))
            .ToArray();
        var (variants, found) = await MapAsync(
            tenantId, items, selected, items.Select(product => product.ImageFileId), cancellationToken);

        // Un producto activo siempre tiene al menos una variante activa (catalog.variant.active_required
        // en el PUT); si alguna vez no la tuviera, sale con la lista vacía y no se esconde: el total
        // de la página cuenta productos y tiene que cuadrar con lo que se pinta.
        var byProduct = variants.ToLookup(variant => variant.ProductId);
        return (items.Select(product => new PosCatalogProductRef(
            product.Id.Value,
            product.Name,
            product.OptionIds.Count > 0,
            CatalogVariantReads.ImageUrlOf(product.ImageFileId, found, tenantId),
            byProduct[product.Id.Value].ToArray())).ToList(), total);
    }

    // Enfoque de revisión 4: un texto que es el código de barras de una variante y el Code de otra
    // es el de barras. El lector de la caja lee códigos de barras; el Code lo teclea el cajero, y
    // un código de barras guardado es una afirmación más fuerte sobre qué unidad tiene en la mano.
    //
    // Salvo que esa variante esté inactiva y el Code sea de una activa (decisión del owner,
    // 2026-10-21): sin esto, un código de barras puesto en una variante que después se apagó le
    // escondía a la caja un Code que antes encontraba (anti-objetivo §1 del spec). Si las dos están
    // inactivas, gana el código de barras, como siempre. «Activa» es variante y producto activos; el
    // precio en la moneda de la caja no se mira acá, porque el lookup no la conoce.
    public async Task<PosProductRef?> FindByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken)
    {
        var byBarcode = await products.FindVariantByBarcodeAsync(tenantId, code, cancellationToken);
        PosProductRef? barcodeRef = null;
        if (byBarcode is not null)
        {
            var variant = byBarcode.Variants.Single(candidate => candidate.Barcode == code);
            barcodeRef = (await MapAsync(tenantId, [byBarcode], [(byBarcode, variant)], [], cancellationToken)).Refs[0];
            if (barcodeRef.IsActive)
            {
                return barcodeRef;
            }
        }

        var byCode = await products.FindVariantByCodeAsync(tenantId, code, cancellationToken);
        if (byCode is null)
        {
            return barcodeRef;
        }

        var coded = byCode.Variants.Single(candidate => candidate.Code == code);
        var codeRef = (await MapAsync(tenantId, [byCode], [(byCode, coded)], [], cancellationToken)).Refs[0];
        return barcodeRef is null || codeRef.IsActive ? codeRef : barcodeRef;
    }

    public async Task<IReadOnlyDictionary<Guid, PosProductRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return new Dictionary<Guid, PosProductRef>();
        }

        var wanted = variantIds.ToHashSet();
        var found = await products.FindVariantsByIdsAsync(
            tenantId, wanted.Select(id => new VariantId(id)).ToArray(), cancellationToken);
        var selected = CatalogVariantReads.Select(found, wanted);
        return (await MapAsync(tenantId, found, selected, [], cancellationToken)).Refs
            .ToDictionary(variant => variant.VariantId);
    }

    // `extraFileIds`: las portadas de los productos de la cuadrícula viajan en la misma llamada de
    // imágenes que las fotos de las variantes, y vuelven en `Images` para resolverlas.
    private async Task<(IReadOnlyList<PosProductRef> Refs, IReadOnlyDictionary<Guid, ProductImageRef> Images)> MapAsync(
        Guid tenantId,
        IReadOnlyList<Product> loaded,
        IReadOnlyList<(Product Product, ProductVariant Variant)> selected,
        IEnumerable<Guid?> extraFileIds,
        CancellationToken cancellationToken)
    {
        var percentages = await CatalogVariantReads.LoadTaxPercentagesAsync(taxRates, tenantId, loaded, cancellationToken);
        var valueNames = await CatalogVariantReads.LoadValueNamesAsync(options, tenantId, loaded, cancellationToken);
        var found = await CatalogVariantReads.LoadImagesAsync(
            images,
            selected.Select(pair => CatalogVariantReads.ImageFileIdOf(pair.Product, pair.Variant)).Concat(extraFileIds),
            cancellationToken);

        var refs = selected.Select(pair => new PosProductRef(
            pair.Variant.Id.Value,
            pair.Product.Id.Value,
            pair.Variant.Code,
            pair.Variant.Barcode,
            CatalogVariantReads.DisplayNameOf(pair.Product, pair.Variant, valueNames),
            CatalogVariantReads.IsSellable(pair.Product, pair.Variant),
            CatalogVariantReads.PricesOf(pair.Variant),
            // Sin tasa, o con una inexistente, cotiza con 0 % (QuotationProductPricingResolver).
            pair.Product.TaxRateId is { } id && percentages.TryGetValue(id, out var percentage) ? percentage : 0,
            // Mismas reglas que QuotationProductLookup: del tenant y disponible (preflight F-16);
            // un archivo en cuarentena o pendiente no debe llegar a la pantalla.
            CatalogVariantReads.ImageUrlOf(
                CatalogVariantReads.ImageFileIdOf(pair.Product, pair.Variant), found, tenantId))).ToList();
        return (refs, found);
    }
}

using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Lo que el mapeo del detalle necesita de afuera del agregado: los nombres de las opciones y sus
/// valores (de la biblioteca) y los archivos de la galería (de Storage, por el puerto). No son del
/// agregado —<c>Product</c> guarda ids—, así que entran resueltos y el mapeo no necesita puertos.
/// </summary>
/// <param name="TenantId">Se vuelve a comparar contra el tenant de cada archivo. Ver
/// <see cref="ProductMapping.ToDtosAsync"/>.</param>
/// <param name="UsedVariantIds">Las variantes que alguna línea vende (<see cref="IVariantUsageChecker"/>):
/// de acá sale <c>CanDelete</c>. Null es «no se consultó» y deja <c>CanDelete</c> en false: ofrecer
/// «Eliminar» sin saberlo sería prometer un borrado que el PUT puede convertir en
/// desactivación.</param>
internal sealed record ProductReadLookups(
    Guid TenantId,
    IReadOnlyDictionary<Guid, OptionDefinition> Options,
    IReadOnlyDictionary<Guid, ProductImageRef> Images,
    IReadOnlySet<Guid>? UsedVariantIds = null)
{
    /// <summary>Sin nombres de opción ni URLs: para quien mapea en memoria (pruebas).</summary>
    public static ProductReadLookups Empty(Guid tenantId) =>
        new(tenantId, new Dictionary<Guid, OptionDefinition>(), new Dictionary<Guid, ProductImageRef>());

    public bool CanDelete(Guid variantId) => UsedVariantIds is { } used && !used.Contains(variantId);

    // Un archivo que desapareció de Storage, que nunca se publicó, que es de otro tenant o que no
    // está disponible (en cuarentena o pendiente) deja la foto sin URL — no sin foto. El fileId sigue
    // viajando: el cliente lo necesita para el PUT. Misma regla que CatalogVariantReads.ImageUrlOf
    // (Bootstrapper), para que el detalle, la grilla y el selector no muestren un archivo que las
    // líneas, el POS y el PDF esconden.
    public string? UrlOf(Guid? fileId) =>
        fileId is { } id
            && Images.TryGetValue(id, out var image)
            && image.TenantId == TenantId
            && image.IsAvailable
            ? image.PublicUrl
            : null;
}

internal static class ProductMapping
{
    public static ProductDto ToDto(this Product product, ProductReadLookups lookups)
    {
        var gallery = product.Gallery;
        var galleryById = gallery.ToDictionary(image => image.Id);
        var mainImageUrl = lookups.UrlOf(product.ImageFileId);
        var valueNames = ValueNamesOf(product, lookups);

        return new ProductDto(
            product.Id.Value,
            product.Name,
            product.IsActive,
            product.Description,
            product.ImageFileId,
            mainImageUrl,
            product.TaxRateId?.Value,
            OptionsOf(product, lookups),
            product.Variants
                .Select(variant => ToResponse(product, variant, galleryById, valueNames, mainImageUrl, lookups))
                .ToArray(),
            gallery
                .Select(image => new ProductGalleryImageResponse(image.Id, image.FileId, lookups.UrlOf(image.FileId)))
                .ToArray(),
            product.Version,
            product.CreatedAt,
            product.UpdatedAt);
    }

    /// <summary>
    /// Mapea una colección resolviendo las URLs en **una sola** consulta al puerto de imágenes y
    /// los nombres de opción en **una sola** consulta a la biblioteca.
    ///
    /// Es la razón de ser de `CAT-05b`: sin esto, el cliente que pinta N productos tiene que pedir
    /// N URLs de descarga. Con la galería el lote son todas las fotos de todos los productos; un
    /// catálogo sin imágenes no paga nada.
    ///
    /// **Recibe el `tenantId` y vuelve a comprobarlo, aunque el resolver ya lo haya hecho en la
    /// escritura.** No es redundancia: `CAT-05a` cerró la escritura, pero **no borra lo que ya
    /// estaba**. Cualquier producto cargado antes de ese slice pudo guardar el archivo de otro
    /// tenant, y esa fila sigue en la base (la migración de variantes la pasó a la galería). Sin
    /// esta comprobación, la escritura queda cerrada y la lectura abierta.
    ///
    /// <c>CanDelete</c> de cada variante sale de **una** consulta de uso por lote
    /// (<paramref name="usageChecker"/>). Null sólo para productos que se acaban de crear en esta
    /// misma petición: ninguna línea puede vender una variante que no existía, así que todas se
    /// pueden borrar sin preguntarle a nadie.
    /// </summary>
    public static async Task<IReadOnlyList<ProductDto>> ToDtosAsync(
        this IEnumerable<Product> products,
        IProductImageLookup imageLookup,
        IOptionDefinitionRepository optionRepository,
        IVariantUsageChecker? usageChecker,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var materialized = products as IReadOnlyList<Product> ?? products.ToArray();
        var lookups = await LoadLookupsAsync(
            materialized, imageLookup, optionRepository, tenantId, cancellationToken);

        var variantIds = materialized
            .SelectMany(product => product.Variants.Select(variant => variant.Id.Value))
            .ToArray();
        lookups = lookups with
        {
            UsedVariantIds = usageChecker is null
                ? new HashSet<Guid>()
                : await usageChecker.FindUsedAsync(tenantId, variantIds, cancellationToken),
        };

        return materialized.Select(product => product.ToDto(lookups)).ToArray();
    }

    public static async Task<ProductReadLookups> LoadLookupsAsync(
        IReadOnlyCollection<Product> products,
        IProductImageLookup imageLookup,
        IOptionDefinitionRepository optionRepository,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var fileIds = products
            .SelectMany(product => product.Gallery.Select(image => image.FileId))
            .Distinct()
            .ToArray();
        var images = fileIds.Length == 0
            ? new Dictionary<Guid, ProductImageRef>()
            : await imageLookup.FindManyAsync(fileIds, cancellationToken);

        var optionIds = products
            .SelectMany(product => product.OptionIds)
            .Distinct()
            .Select(id => new OptionDefinitionId(id))
            .ToArray();
        var options = optionIds.Length == 0
            ? []
            : await optionRepository.ListByIdsAsync(tenantId, optionIds, cancellationToken);

        return new ProductReadLookups(
            tenantId,
            options.ToDictionary(option => option.Id.Value),
            images);
    }

    // Valor → nombre, de las opciones del producto. Un valor sin nombre (una opción que la lectura
    // no encontró) no rompe el detalle: el nombre visible lo omite.
    private static Dictionary<Guid, string> ValueNamesOf(Product product, ProductReadLookups lookups)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var optionId in product.OptionIds)
        {
            if (!lookups.Options.TryGetValue(optionId, out var option))
            {
                continue;
            }

            foreach (var value in option.Values)
            {
                names[value.Id] = value.Value;
            }
        }

        return names;
    }

    private static ProductOptionResponse[] OptionsOf(Product product, ProductReadLookups lookups)
    {
        var used = product.Variants.SelectMany(variant => variant.OptionValueIds).ToHashSet();

        return product.OptionIds
            .Select(optionId => lookups.Options.TryGetValue(optionId, out var option)
                ? new ProductOptionResponse(
                    optionId,
                    option.Name,
                    option.Values
                        .Where(value => used.Contains(value.Id))
                        .OrderBy(value => value.Position)
                        .Select(value => new ProductOptionValueResponse(value.Id, value.Value))
                        .ToArray())
                : new ProductOptionResponse(optionId, string.Empty, []))
            .ToArray();
    }

    private static ProductVariantResponse ToResponse(
        Product product,
        ProductVariant variant,
        Dictionary<Guid, ProductGalleryImage> galleryById,
        Dictionary<Guid, string> valueNames,
        string? mainImageUrl,
        ProductReadLookups lookups)
    {
        Guid? galleryFileId = variant.GalleryImageId is { } imageId && galleryById.TryGetValue(imageId, out var image)
            ? image.FileId
            : null;

        return new ProductVariantResponse(
            variant.Id.Value,
            DisplayNameOf(product.Name, variant.OptionValueIds, valueNames),
            variant.Code,
            variant.Barcode,
            variant.OptionValueIds,
            PricesOf(variant),
            // Ordered here, and not left to whatever order the aggregate or the database hands
            // over: the grid paints the tiers from the smallest up, and neither the write order
            // nor the row order the database returns is a contract it can rely on. Same criterion
            // the Excel export already applies (`ClosedXmlProductExportBuilder`).
            variant.PriceScales.OrderBy(scale => scale.FromUnit).Select(scale => ToResponse(scale, variant)).ToArray(),
            variant.PackagingUnits.ToArray(),
            variant.GalleryImageId,
            galleryFileId,
            galleryFileId is { } fileId ? lookups.UrlOf(fileId) : mainImageUrl,
            variant.IsActive,
            // Spec §3.3: sin líneas que la vendan se puede borrar (omitiéndola del PUT); con
            // líneas, el PUT la desactiva. Ver ProductVariantResponse.
            CanDelete: lookups.CanDelete(variant.Id.Value));
    }

    // Catalogue order (spec D10), not insertion order: the form and the product table paint one
    // column per currency in that order.
    private static Dictionary<string, decimal> PricesOf(ProductVariant variant) =>
        InCatalogueOrder(variant.Prices.Select(price => KeyValuePair.Create(price.Currency, price.Amount)));

    private static Dictionary<string, decimal> InCatalogueOrder(IEnumerable<KeyValuePair<string, decimal>> prices) =>
        prices
            .OrderBy(price => Currencies.OrderOf(price.Key))
            .ToDictionary(price => price.Key, price => price.Value);

    private static PriceScaleResponse ToResponse(PriceScale scale, ProductVariant variant) => ToResponse(
        scale.Id.Value, scale.FromUnit, scale.ToUnit, scale.Discount,
        scale.Restriction, scale.Multiple, scale.AllowGrouping,
        PricesOf(variant));

    // Derived, never stored (spec D4): one final per currency the variant has. `prices` ya viene en
    // orden de catálogo.
    private static PriceScaleResponse ToResponse(
        Guid id,
        int fromUnit,
        int toUnit,
        decimal discount,
        PriceScaleRestriction? restriction,
        int? multiple,
        bool allowGrouping,
        Dictionary<string, decimal> prices) => new(
        id, fromUnit, toUnit, discount,
        restriction?.ToWireValue(), multiple,
        allowGrouping,
        prices.ToDictionary(price => price.Key, price => PriceScale.FinalFor(price.Value, discount)!.Value));

    /// <summary>
    /// El nombre visible (spec §3.4) de una variante a partir de los ids de sus valores. Un valor
    /// sin nombre (una opción que la lectura no encontró) se omite: el nombre queda corto, no se
    /// rompe la pantalla. Lo comparten el detalle y el selector del listado.
    /// </summary>
    public static string DisplayNameOf(
        string productName,
        IEnumerable<Guid> optionValueIds,
        IReadOnlyDictionary<Guid, string> valueNames) =>
        VariantDisplayName.For(
            productName,
            optionValueIds.Where(valueNames.ContainsKey).Select(id => valueNames[id]).ToArray());

    /// <summary>
    /// Valor de opción → texto de **toda** la biblioteca del tenant, en una consulta. Para quien lee
    /// variantes sin haber cargado sus productos y por eso no sabe qué opciones usan: la biblioteca
    /// de un tenant son pocas opciones, así que pedirla entera cuesta menos que descubrir cuáles
    /// hacen falta. Mismo criterio que <c>CatalogVariantReads</c> en Bootstrapper, que Catalog no
    /// puede referenciar.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> LoadValueNamesAsync(
        IOptionDefinitionRepository optionRepository,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var library = await optionRepository.ListAsync(tenantId, cancellationToken);
        return library
            .SelectMany(option => option.Values)
            .ToDictionary(value => value.Id, value => value.Value);
    }

    /// <summary>
    /// La variante del selector (<see cref="ProductListItemDto.Variants"/>) desde su lectura
    /// proyectada. La foto es la propia o, sin ella, la principal del producto, igual que en el
    /// detalle; las escalas y los precios salen en el mismo orden y con los mismos finales.
    /// </summary>
    public static ProductSellableVariantDto ToSellableDto(
        this SellableVariantSummary variant,
        string productName,
        IReadOnlyDictionary<Guid, string> valueNames,
        string? mainImageUrl,
        ProductReadLookups lookups)
    {
        var prices = InCatalogueOrder(variant.Prices);

        return new ProductSellableVariantDto(
            variant.Id.Value,
            DisplayNameOf(productName, variant.OptionValueIds, valueNames),
            variant.Code,
            variant.Barcode,
            prices,
            variant.Scales
                .OrderBy(scale => scale.FromUnit)
                .Select(scale => ToResponse(
                    scale.Id, scale.FromUnit, scale.ToUnit, scale.Discount,
                    scale.Restriction, scale.Multiple, scale.AllowGrouping, prices))
                .ToArray(),
            variant.PackagingUnits.ToArray(),
            variant.GalleryFileId is { } fileId ? lookups.UrlOf(fileId) : mainImageUrl);
    }
}

using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Bootstrapper;

/// <summary>
/// Lo que los adaptadores de Catalog hacia Quotations, Pos y Shopify leen igual de una variante
/// (spec 2026-10-20 §3.4, §4 y §5): su nombre visible, su foto, si se vende y la tasa de su
/// producto. Vive en un solo lugar para que una línea de cotización, el ticket del POS y un pedido
/// de Shopify no puedan nombrar distinto la misma variante.
///
/// Todo en lote: los adaptadores cargan los productos de una pantalla y piden opciones, fotos y
/// tasas una vez por lote, no una por línea.
/// </summary>
internal static class CatalogVariantReads
{
    /// <summary>
    /// Valor de opción → texto, de las opciones de esos productos, en una consulta (o ninguna, si
    /// ningún producto tiene opciones). Sólo del tenant: la biblioteca filtra por él.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> LoadValueNamesAsync(
        IOptionDefinitionRepository options,
        Guid tenantId,
        IEnumerable<Product> products,
        CancellationToken cancellationToken)
    {
        var optionIds = products
            .SelectMany(product => product.OptionIds)
            .Distinct()
            .Select(id => new OptionDefinitionId(id))
            .ToArray();
        if (optionIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var found = await options.ListByIdsAsync(tenantId, optionIds, cancellationToken);
        return found
            .SelectMany(option => option.Values)
            .ToDictionary(value => value.Id, value => value.Value);
    }

    /// <summary>
    /// El nombre visible (spec §3.4), con <see cref="VariantDisplayName"/>. Un valor cuya opción no
    /// se encontró se omite, igual que en el detalle del producto (<c>ProductMapping</c>): el
    /// nombre queda corto, no se rompe la pantalla.
    /// </summary>
    public static string DisplayNameOf(
        Product product,
        ProductVariant variant,
        IReadOnlyDictionary<Guid, string> valueNames) =>
        DisplayNameOf(product.Name, variant.OptionValueIds, valueNames);

    /// <summary>
    /// Lo mismo cuando quien llama sólo tiene el nombre del producto y los ids de los valores, y
    /// no el agregado ni la variante (el reporte de cambios de precio proyecta esos datos de
    /// <c>product_variants</c> sin materializar entidades).
    /// </summary>
    public static string DisplayNameOf(
        string productName,
        IReadOnlyList<Guid> optionValueIds,
        IReadOnlyDictionary<Guid, string> valueNames) =>
        VariantDisplayName.For(
            productName,
            optionValueIds.Where(valueNames.ContainsKey).Select(id => valueNames[id]).ToArray());

    /// <summary>
    /// Valor de opción → texto de **toda** la biblioteca del tenant, en una consulta. Para quien
    /// lee variantes sin haber cargado sus productos y por eso no sabe qué opciones usan: la
    /// biblioteca de un tenant son pocas opciones (<see cref="IOptionDefinitionRepository.ListAsync"/>
    /// la recorre entera), así que pedirla completa cuesta menos que descubrir cuáles hacen falta.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> LoadValueNamesAsync(
        IOptionDefinitionRepository options,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var library = await options.ListAsync(tenantId, cancellationToken);
        return library
            .SelectMany(option => option.Values)
            .ToDictionary(value => value.Id, value => value.Value);
    }

    /// <summary>
    /// El archivo de la foto de la variante; sin foto propia, el de la principal del producto.
    /// Una foto propia que después resulta no disponible **no** cae a la principal: mismo criterio
    /// que el detalle del producto (<c>ProductMapping</c>), para que el formulario y la línea
    /// muestren lo mismo.
    /// </summary>
    public static Guid? ImageFileIdOf(Product product, ProductVariant variant) =>
        variant.GalleryImageId is { } imageId
            && product.Gallery.FirstOrDefault(image => image.Id == imageId) is { } own
            ? own.FileId
            : product.ImageFileId;

    /// <summary>Los archivos de esas fotos en una sola llamada; ninguna si no hay fotos.</summary>
    public static async Task<IReadOnlyDictionary<Guid, ProductImageRef>> LoadImagesAsync(
        IProductImageLookup images,
        IEnumerable<Guid?> fileIds,
        CancellationToken cancellationToken)
    {
        var ids = fileIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        return ids.Length == 0
            ? new Dictionary<Guid, ProductImageRef>()
            : await images.FindManyAsync(ids, cancellationToken);
    }

    /// <summary>
    /// La URL pública, sólo si el archivo es del tenant y está disponible (preflight F-16): uno en
    /// cuarentena, pendiente o ajeno sale como null. La referencia ajena se ignora, no se rechaza:
    /// es una lectura de algo ya guardado, no un request que corregir.
    /// </summary>
    public static string? ImageUrlOf(
        Guid? fileId,
        IReadOnlyDictionary<Guid, ProductImageRef> images,
        Guid tenantId) =>
        fileId is { } id
            && images.TryGetValue(id, out var image)
            && image.TenantId == tenantId
            && image.IsAvailable
            ? image.PublicUrl
            : null;

    /// <summary>
    /// Vendible: el producto y la variante activos. Una variante activa de un producto dado de baja
    /// no se vende, igual que antes de las variantes no se vendía el producto.
    /// </summary>
    public static bool IsSellable(Product product, ProductVariant variant) =>
        product.IsActive && variant.IsActive;

    public static IReadOnlyDictionary<string, decimal> PricesOf(ProductVariant variant) =>
        variant.Prices.ToDictionary(price => price.Currency, price => price.Amount);

    /// <summary>
    /// El porcentaje de cada tasa que usan esos productos, una lectura por tasa distinta. Una tasa
    /// que ya no existe no aparece: quien llama decide si eso es null o 0.
    /// </summary>
    public static async Task<IReadOnlyDictionary<TaxRateId, int>> LoadTaxPercentagesAsync(
        ITaxRateRepository taxRates,
        Guid tenantId,
        IEnumerable<Product> products,
        CancellationToken cancellationToken)
    {
        var percentages = new Dictionary<TaxRateId, int>();
        foreach (var taxRateId in products
            .Where(product => product.TaxRateId.HasValue)
            .Select(product => product.TaxRateId!.Value)
            .Distinct())
        {
            var taxRate = await taxRates.FindAsync(tenantId, taxRateId, cancellationToken);
            if (taxRate is not null)
            {
                percentages[taxRateId] = taxRate.Percentage;
            }
        }

        return percentages;
    }

    /// <summary>Las variantes pedidas, cada una con su producto. Un id que ningún producto del lote
    /// trae (no existe o es de otro tenant) no aparece.</summary>
    public static IReadOnlyList<(Product Product, ProductVariant Variant)> Select(
        IEnumerable<Product> products,
        IReadOnlySet<Guid> variantIds) =>
        products
            .SelectMany(product => product.Variants
                .Where(variant => variantIds.Contains(variant.Id.Value))
                .Select(variant => (product, variant)))
            .ToArray();
}

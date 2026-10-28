namespace Modules.Catalog.Domain;

/// <summary>
/// Una variante de un producto (spec 2026-10-20 §3.3): lo que se vende. Entidad dentro del
/// agregado <see cref="Product"/>, sin repositorio propio; sólo el producto la crea o la cambia.
///
/// Se llevó del producto el código, los precios, las escalas y los empaques, con las mismas reglas
/// y los mismos códigos de error (<c>catalog.product.price_required</c>,
/// <c>catalog.product.price_scale.*</c>, <c>catalog.product.packaging_units.*</c>): son contrato
/// con el frontend, y un producto sin opciones se sigue viendo como antes.
/// </summary>
public sealed class ProductVariant
{
    /// <summary>Espeja el ancho de columna de <c>code</c>, igual que antes en el producto.</summary>
    public const int CodeMaxLength = Product.CodeMaxLength;

    /// <summary>Un GTIN-14 cabe en 14; 64 deja lugar a códigos internos sin acercarse a un
    /// valor absurdo (spec §10 lo deja al builder).</summary>
    public const int BarcodeMaxLength = 64;

    private readonly List<ProductPrice> _prices = [];
    private readonly List<PriceScale> _priceScales = [];
    private readonly List<ProductVariantOptionValue> _optionValues = [];

    // EF Core materializa por acá.
    private ProductVariant()
    {
        Code = string.Empty;
        CombinationKey = string.Empty;
    }

    private ProductVariant(VariantId id, ProductId productId, Guid tenantId)
    {
        Id = id;
        ProductId = productId;
        TenantId = tenantId;
        Code = string.Empty;
        CombinationKey = string.Empty;
    }

    public VariantId Id { get; private set; }

    public ProductId ProductId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Único por tenant entre todas las variantes; la unicidad entre productos la da el
    /// índice de base, y dentro de un mismo producto la revisa <see cref="Product"/>.</summary>
    public string Code { get; private set; }

    /// <summary>Opcional; único por tenant cuando existe. En blanco se guarda como null.</summary>
    public string? Barcode { get; private set; }

    /// <summary>Un valor por cada opción del producto (<see cref="Product.OptionIds"/>), en el
    /// mismo orden. Vacío en la variante por defecto de un producto sin opciones.</summary>
    public IReadOnlyList<Guid> OptionValueIds =>
        _optionValues.OrderBy(value => value.Position).Select(value => value.OptionValueId).ToArray();

    /// <summary>
    /// <see cref="OptionValueIds"/> en una sola cadena, en orden (vacía en la variante por defecto).
    /// Es lo que compara <see cref="Product"/> para no repetir una combinación entre variantes
    /// activas, y lo que indexa <c>IX_product_variants_product_combination</c> para buscar una
    /// variante por su combinación sin juntar la tabla de valores.
    /// </summary>
    public string CombinationKey { get; private set; }

    public IReadOnlyCollection<ProductPrice> Prices => _prices;

    /// <summary>Las monedas en que la variante se puede cotizar y vender, en orden ordinal.</summary>
    public IReadOnlyCollection<string> PricedCurrencies =>
        _prices.Select(price => price.Currency).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Null cuando la variante no tiene precio en esa moneda. Nunca una conversión.</summary>
    public decimal? PriceIn(string currency) =>
        _prices.Find(price => price.Currency == currency)?.Amount;

    /// <summary>Se reemplazan enteras en cada <see cref="Product.Update"/>: el PUT reemplaza el
    /// recurso completo, así que una escala que no viene deja de existir.</summary>
    public IReadOnlyCollection<PriceScale> PriceScales => _priceScales;

    /// <summary>
    /// Los empaques en que viene la variante, en unidades, ascendentes. Entre 1 y
    /// <see cref="Product.MaxPackagingUnitValue"/>, a lo sumo <see cref="Product.MaxPackagingUnits"/>
    /// y sin repetidos. No pueden estar vacíos mientras una escala use
    /// <see cref="PriceScaleRestriction.PackagingUnit"/>.
    /// </summary>
    public IReadOnlyList<int> PackagingUnits { get; private set; } = [];

    /// <summary>La foto de la galería del producto que muestra esta variante
    /// (<see cref="ProductGalleryImage.Id"/>); sin ella se usa la principal del producto.</summary>
    public Guid? GalleryImageId { get; private set; }

    /// <summary>Una variante inactiva no se ofrece al vender.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Base 0; el orden en que el producto presenta sus variantes.</summary>
    public int Position { get; private set; }

    internal static ProductVariant Create(VariantId id, ProductId productId, Guid tenantId) =>
        new(id, productId, tenantId);

    /// <summary>
    /// Aplica lo que manda el formulario. El código ya llega normalizado por <see cref="Product"/>,
    /// que lo necesita antes para comparar entre variantes.
    /// </summary>
    internal void Apply(
        ProductVariantInput input,
        string normalizedCode,
        string? normalizedBarcode,
        int position,
        Guid? galleryImageId)
    {
        Code = normalizedCode;
        Barcode = normalizedBarcode;
        ReplaceOptionValues(input.OptionValueIds);
        CombinationKey = CombinationKeyOf(input.OptionValueIds);
        GalleryImageId = galleryImageId;
        IsActive = input.IsActive;
        Position = position;
        ApplyPricing(input.Pricing);
    }

    /// <summary>Ver <see cref="Product.ApplyCopiedPriceScales"/>.</summary>
    internal void ApplyCopiedPriceScales(IReadOnlyCollection<PriceScaleInput> scales)
    {
        _priceScales.Clear();
        foreach (var scale in scales)
        {
            _priceScales.Add(PriceScale.CreateIncomplete(Id, TenantId, scale));
        }
    }

    internal static string CombinationKeyOf(IReadOnlyList<Guid> optionValueIds) =>
        string.Join(',', optionValueIds);

    // En el lugar, por posición, como Product.ReplaceOptions.
    private void ReplaceOptionValues(IReadOnlyList<Guid> optionValueIds)
    {
        _optionValues.RemoveAll(value => value.Position >= optionValueIds.Count);
        for (var position = 0; position < optionValueIds.Count; position++)
        {
            var existing = _optionValues.Find(value => value.Position == position);
            if (existing is null)
            {
                _optionValues.Add(new ProductVariantOptionValue(optionValueIds[position], position));
            }
            else
            {
                existing.OptionValueId = optionValueIds[position];
            }
        }
    }

    internal static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new CatalogDomainException(
                "catalog.product.code_required",
                "The product code is required.");
        }

        var trimmed = code.Trim();
        return trimmed.Length > CodeMaxLength
            ? throw new CatalogDomainException(
                "catalog.product.code_too_long",
                $"The product code cannot exceed {CodeMaxLength} characters.")
            : trimmed;
    }

    internal static string? NormalizeBarcode(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        var trimmed = barcode.Trim();
        return trimmed.Length > BarcodeMaxLength
            ? throw new CatalogDomainException(
                "catalog.variant.barcode_too_long",
                $"The variant barcode cannot exceed {BarcodeMaxLength} characters.")
            : trimmed;
    }

    private void ApplyPricing(ProductPricing pricing)
    {
        // Incondicional: toda variante necesita precio en al menos una moneda (CAT-09, spec D3).
        if (pricing.Prices.Count == 0)
        {
            throw new CatalogDomainException(
                "catalog.product.price_required",
                "The product requires a price in at least one currency.");
        }

        foreach (var (currency, amount) in pricing.Prices)
        {
            EnsureCurrencyCodeShape(currency);
            EnsurePriceNotNegative(amount);
        }

        ReplacePrices(pricing.Prices);
        PackagingUnits = NormalizePackagingUnits(pricing.PackagingUnits);

        ReplaceScales(pricing.Scales, pricing.KeepsIncompleteScales);

        // Después de las escalas y no antes: un error propio de una escala (rango, descuento,
        // múltiplo) dice más que éste, que sólo aplica si la escala ya es válida por su cuenta.
        if (PackagingUnits.Count == 0
            && _priceScales.Any(scale => scale.Restriction == PriceScaleRestriction.PackagingUnit))
        {
            throw new CatalogDomainException(
                "catalog.product.packaging_units_required",
                "A price scale restricted to the packaging unit requires the product to have at least one packaging unit.");
        }
    }

    // En el lugar y no Clear()+Add: las filas propias se identifican por (dueño, moneda), y EF
    // seguiría una fila borrada y una agregada con la misma clave en el mismo SaveChanges.
    private void ReplacePrices(IReadOnlyDictionary<string, decimal> prices)
    {
        _prices.RemoveAll(price => !prices.ContainsKey(price.Currency));
        foreach (var (currency, amount) in prices)
        {
            var existing = _prices.Find(price => price.Currency == currency);
            if (existing is null)
            {
                _prices.Add(new ProductPrice(currency, amount));
            }
            else
            {
                existing.ChangeAmount(amount);
            }
        }
    }

    private void ReplaceScales(IReadOnlyCollection<PriceScaleInput> scales, bool keepsIncompleteScales)
    {
        _priceScales.Clear();
        foreach (var scale in scales)
        {
            _priceScales.Add(keepsIncompleteScales && scale.Restriction is null
                ? PriceScale.CreateIncomplete(Id, TenantId, scale)
                : PriceScale.Create(Id, TenantId, scale));
        }
    }

    // Sólo la forma: el catálogo de monedas lo revisa Currencies.Normalize en la aplicación. Un
    // código sin normalizar que llega al agregado es un error de programación, no un 422.
    private static void EnsureCurrencyCodeShape(string currency)
    {
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                $"Currency '{currency}' must arrive normalised (three upper-case letters).",
                nameof(currency));
        }
    }

    private static void EnsurePriceNotNegative(decimal value)
    {
        if (value < 0m)
        {
            throw new CatalogDomainException(
                "catalog.product.price_negative",
                "A product price cannot be negative.");
        }
    }

    // Rechaza en vez de corregir: un cero, un negativo o un repetido es un error de tipeo, y
    // descartarlo en silencio le guardaría al usuario algo distinto de lo que cree haber cargado.
    // Ordenar sí se hace aquí — el orden no cambia el significado del conjunto. Los topes acotan lo
    // que la regla de empaque de Quotations recorre en cada cotización.
    private static int[] NormalizePackagingUnits(IReadOnlyCollection<int> packagingUnits)
    {
        if (packagingUnits.Count > Product.MaxPackagingUnits)
        {
            throw new CatalogDomainException(
                "catalog.product.packaging_units.too_many",
                $"A product cannot have more than {Product.MaxPackagingUnits} packaging units.");
        }

        if (packagingUnits.Any(unit => unit is <= 0 or > Product.MaxPackagingUnitValue))
        {
            throw new CatalogDomainException(
                "catalog.product.packaging_units.invalid",
                $"Every packaging unit must be between 1 and {Product.MaxPackagingUnitValue}.");
        }

        var sorted = packagingUnits.Order().ToArray();
        for (var index = 1; index < sorted.Length; index++)
        {
            if (sorted[index] == sorted[index - 1])
            {
                throw new CatalogDomainException(
                    "catalog.product.packaging_units.duplicated",
                    "The packaging units cannot repeat a value.");
            }
        }

        return sorted;
    }
}

/// <summary>
/// Una variante como la manda el formulario en el POST/PUT del producto.
/// </summary>
public sealed record ProductVariantInput
{
    /// <summary>
    /// El id de una variante ya guardada, o null para una nueva: el PUT empareja por id (spec
    /// §3.3.1). <see cref="Product.Create"/> lo ignora, porque la variante por defecto toma el id
    /// del producto.
    /// </summary>
    public Guid? Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string? Barcode { get; init; }

    /// <summary>Un valor por opción del producto, en el orden de las opciones.</summary>
    public IReadOnlyList<Guid> OptionValueIds { get; init; } = [];

    public ProductPricing Pricing { get; init; } = new();

    /// <summary>
    /// La foto de la variante, por el id del archivo y no de la fila de galería: una foto que se
    /// sube en el mismo PUT todavía no tiene fila. Tiene que estar en la galería que viaja en el
    /// mismo PUT (<c>catalog.variant.image_not_in_gallery</c>).
    /// </summary>
    public Guid? GalleryFileId { get; init; }

    public bool IsActive { get; init; } = true;
}

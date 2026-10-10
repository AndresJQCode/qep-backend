namespace Modules.Catalog.Domain;

/// <summary>
/// Un tramo de precio por cantidad de un producto (CAT-09). No es un agregado propio: nace,
/// vive y muere con su <see cref="Product"/> — no tiene repositorio ni caso de uso propio, y
/// <see cref="Create"/> es <c>internal</c> porque sólo <c>Product</c> la construye.
/// </summary>
public sealed class PriceScale
{
    public const int MinDiscount = 0;
    public const int MaxDiscount = 100;

    // EF Core materializa por acá.
    private PriceScale() { }

    private PriceScale(
        PriceScaleId id,
        ProductId productId,
        Guid tenantId,
        int fromUnit,
        int toUnit,
        decimal discount,
        PriceScaleRestriction? restriction,
        int? multiple,
        bool allowGrouping)
    {
        Id = id;
        ProductId = productId;
        TenantId = tenantId;
        FromUnit = fromUnit;
        ToUnit = toUnit;
        Discount = discount;
        Restriction = restriction;
        Multiple = multiple;
        AllowGrouping = allowGrouping;
    }

    public PriceScaleId Id { get; private set; }

    public ProductId ProductId { get; private set; }

    public Guid TenantId { get; private set; }

    public int FromUnit { get; private set; }

    public int ToUnit { get; private set; }

    /// <summary>Porcentaje, 0 a 100.</summary>
    public decimal Discount { get; private set; }

    /// <summary>
    /// Null sólo en una escala **incompleta**: la que deja <see cref="CreateIncomplete"/> al
    /// copiar escalas de otro producto, que trae rango y descuento pero no la restricción, porque
    /// ésa depende de cómo se vende cada producto. Nadie puede cotizar el producto hasta que
    /// alguien la complete desde el formulario, que sí la exige (<see cref="Create"/>).
    /// </summary>
    public PriceScaleRestriction? Restriction { get; private set; }

    /// <summary>Sólo cuando <see cref="Restriction"/> es <c>Multiple</c>; null en el otro caso.</summary>
    public int? Multiple { get; private set; }

    /// <summary>Si esta escala permite que las cantidades de varias líneas de una cotización
    /// se sumen para validar el múltiplo. Siempre <c>false</c> cuando
    /// <see cref="Restriction"/> es <c>PackagingUnit</c> — lo hace cumplir
    /// <see cref="Create"/>.</summary>
    public bool AllowGrouping { get; private set; }

    internal static PriceScale Create(
        ProductId productId,
        Guid tenantId,
        PriceScaleInput input)
    {
        ValidateRangeAndDiscount(input);

        if (input.Restriction is null)
        {
            throw new CatalogDomainException(
                "catalog.product.price_scale.restriction_required",
                "The price scale restriction is required.");
        }

        var restriction = input.Restriction.Value;
        int? multiple;

        if (restriction == PriceScaleRestriction.Multiple)
        {
            if (input.Multiple is not (> 0))
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.multiple_required",
                    "A multiple greater than zero is required when the restriction is 'multiple'.");
            }

            multiple = input.Multiple;
        }
        else
        {
            if (input.AllowGrouping)
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.grouping_not_allowed",
                    "Grouping is only available when the restriction is 'multiple'.");
            }

            // Sin número propio: la escala usa los empaques del producto, y que no estén vacíos
            // lo hace cumplir Product, que es el que ve los dos (Product.PackagingUnits).
            if (input.Multiple is not null)
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.multiple_not_allowed",
                    "A multiple is not allowed when the restriction is 'packaging_unit'.");
            }

            multiple = null;
        }

        return new PriceScale(
            PriceScaleId.New(),
            productId,
            tenantId,
            input.FromUnit,
            input.ToUnit,
            input.Discount,
            restriction,
            multiple,
            input.AllowGrouping);
    }

    /// <summary>
    /// Una escala sin restricción, múltiplo ni agrupación: sólo rango y descuento. La usa
    /// **únicamente** la copia de escalas (<see cref="Product.ApplyCopiedPriceScales"/>);
    /// el formulario pasa por <see cref="Create"/>, que sigue exigiendo la restricción.
    ///
    /// Rango y descuento se validan igual que en <see cref="Create"/>: lo incompleto es la
    /// restricción, no el precio. Una entrada que sí trae restricción no se acepta ni se
    /// descarta en silencio — quien la armó creía estar copiando algo que esta escala no guarda,
    /// y eso es un error de programación, no un 422.
    /// </summary>
    internal static PriceScale CreateIncomplete(
        ProductId productId,
        Guid tenantId,
        PriceScaleInput input)
    {
        if (input.Restriction is not null
            || input.Multiple is not null
            || input.AllowGrouping)
        {
            throw new ArgumentException(
                "An incomplete price scale cannot carry a restriction, multiple or grouping.",
                nameof(input));
        }

        ValidateRangeAndDiscount(input);

        return new PriceScale(
            PriceScaleId.New(),
            productId,
            tenantId,
            input.FromUnit,
            input.ToUnit,
            input.Discount,
            restriction: null,
            multiple: null,
            allowGrouping: false);
    }

    private static void ValidateRangeAndDiscount(PriceScaleInput input)
    {
        if (input.FromUnit < 1)
        {
            throw new CatalogDomainException(
                "catalog.product.price_scale.range_invalid",
                "The price scale's starting unit must be at least 1.");
        }

        if (input.ToUnit <= input.FromUnit)
        {
            throw new CatalogDomainException(
                "catalog.product.price_scale.range_invalid",
                "The price scale's ending unit must be greater than its starting unit.");
        }

        if (input.Discount < MinDiscount || input.Discount > MaxDiscount)
        {
            throw new CatalogDomainException(
                "catalog.product.price_scale.discount_out_of_range",
                $"The price scale discount must be between {MinDiscount} and {MaxDiscount}.");
        }
    }

    /// <summary>
    /// The final price of a discount over a base price, rounded to two decimals away from zero.
    /// The single derivation (spec D4): the product response, the Excel export and the contract
    /// migration guard (DropLegacyProductPriceColumns) all go through it or mirror it, so they cannot
    /// disagree on a cent. Public because Infrastructure (the export builder) calls it.
    /// </summary>
    public static decimal? FinalFor(decimal? productBase, decimal discount) =>
        productBase is null
            ? null
            : Math.Round(
                productBase.Value * (1 - discount / 100m), 2, MidpointRounding.AwayFromZero);
}

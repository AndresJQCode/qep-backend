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
        PriceScaleRestriction restriction,
        int? multiple,
        int? packagingUnit,
        decimal? finalUsd,
        decimal? finalCop,
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
        PackagingUnit = packagingUnit;
        FinalUsd = finalUsd;
        FinalCop = finalCop;
        AllowGrouping = allowGrouping;
    }

    public PriceScaleId Id { get; private set; }

    public ProductId ProductId { get; private set; }

    public Guid TenantId { get; private set; }

    public int FromUnit { get; private set; }

    public int ToUnit { get; private set; }

    /// <summary>Porcentaje, 0 a 100.</summary>
    public decimal Discount { get; private set; }

    public PriceScaleRestriction Restriction { get; private set; }

    /// <summary>Sólo cuando <see cref="Restriction"/> es <c>Multiple</c>; null en el otro caso.</summary>
    public int? Multiple { get; private set; }

    /// <summary>Unidades por empaque. Sólo cuando <see cref="Restriction"/> es
    /// <c>PackagingUnit</c>; null en el otro caso.</summary>
    public int? PackagingUnit { get; private set; }

    public decimal? FinalUsd { get; private set; }

    public decimal? FinalCop { get; private set; }

    /// <summary>Si esta escala permite que las cantidades de varias líneas de una cotización
    /// se sumen para validar el múltiplo. Siempre <c>false</c> cuando
    /// <see cref="Restriction"/> es <c>PackagingUnit</c> — lo hace cumplir
    /// <see cref="Create"/>.</summary>
    public bool AllowGrouping { get; private set; }

    /// <param name="productBaseUsd">
    /// El precio base USD del producto dueño, para validar <see cref="FinalUsd"/> contra
    /// base × (1 − descuento%). No es un campo de la escala: vive en <c>Product</c>.
    /// </param>
    /// <param name="productBaseCop">Igual que <paramref name="productBaseUsd"/>, para COP.</param>
    internal static PriceScale Create(
        ProductId productId,
        Guid tenantId,
        PriceScaleInput input,
        decimal? productBaseUsd,
        decimal? productBaseCop)
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

        if (input.Restriction is null)
        {
            throw new CatalogDomainException(
                "catalog.product.price_scale.restriction_required",
                "The price scale restriction is required.");
        }

        var restriction = input.Restriction.Value;
        int? packagingUnit;
        int? multiple;

        if (restriction == PriceScaleRestriction.Multiple)
        {
            if (input.Multiple is not (> 0))
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.multiple_required",
                    "A multiple greater than zero is required when the restriction is 'multiple'.");
            }

            if (input.PackagingUnit is not null)
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.packaging_unit_not_allowed",
                    "A packaging unit is not allowed when the restriction is 'multiple'.");
            }

            multiple = input.Multiple;
            packagingUnit = null;
        }
        else
        {
            if (input.AllowGrouping)
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.grouping_not_allowed",
                    "Grouping is only available when the restriction is 'multiple'.");
            }

            if (input.PackagingUnit is not (> 0))
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.packaging_unit_required",
                    "A packaging unit greater than zero is required when the restriction is 'packaging_unit'.");
            }

            if (input.Multiple is not null)
            {
                throw new CatalogDomainException(
                    "catalog.product.price_scale.multiple_not_allowed",
                    "A multiple is not allowed when the restriction is 'packaging_unit'.");
            }

            packagingUnit = input.PackagingUnit;
            multiple = null;
        }

        if (input.FinalUsd is null && input.FinalCop is null)
        {
            throw new CatalogDomainException(
                "catalog.product.price_scale.final_currency_required",
                "The price scale requires a final price in at least one currency.");
        }

        ValidateFinal(
            input.FinalUsd,
            productBaseUsd,
            input.Discount,
            "catalog.product.price_scale.final_without_base_usd",
            "catalog.product.price_scale.final_mismatch_usd",
            "USD");
        ValidateFinal(
            input.FinalCop,
            productBaseCop,
            input.Discount,
            "catalog.product.price_scale.final_without_base_cop",
            "catalog.product.price_scale.final_mismatch_cop",
            "COP");

        return new PriceScale(
            PriceScaleId.New(),
            productId,
            tenantId,
            input.FromUnit,
            input.ToUnit,
            input.Discount,
            restriction,
            multiple,
            packagingUnit,
            input.FinalUsd,
            input.FinalCop,
            input.AllowGrouping);
    }

    /// <summary>
    /// El precio final lo manda el cliente — no lo calcula el backend — pero el backend lo
    /// valida contra el precio base del producto y el descuento de la escala, con una
    /// tolerancia de redondeo de un centavo. Ver DomainDecisiones: precio final por
    /// cálculo del cliente, back valida.
    /// </summary>
    private static void ValidateFinal(
        decimal? final,
        decimal? productBase,
        decimal discount,
        string withoutBaseCode,
        string mismatchCode,
        string currencyLabel)
    {
        if (final is null)
        {
            return;
        }

        if (productBase is null)
        {
            throw new CatalogDomainException(
                withoutBaseCode,
                $"A final price in {currencyLabel} requires the product to have a base price in {currencyLabel}.");
        }

        var expected = Math.Round(
            productBase.Value * (1 - discount / 100m), 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(final.Value - expected) > 0.01m)
        {
            throw new CatalogDomainException(
                mismatchCode,
                $"The final price in {currencyLabel} does not match the base price and the discount.");
        }
    }
}

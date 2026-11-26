namespace Modules.Catalog.Domain;

/// <summary>
/// Spec 2026-11-22 (D-2, D-9): el precio fijo de una variante en una moneda dentro de una lista. No vive dentro de
/// <see cref="PriceList"/>: una lista puede tener miles y se editan por lote y paginadas, así que cargarlas todas para tocar una
/// sería pagar la tabla entera. Cambiar una sube la versión de su lista (<see cref="PriceList.TouchOverrides"/>), que es el
/// <c>If-Match</c>. La moneda llega normalizada por la aplicación.
/// </summary>
public sealed class PriceListOverride
{
    public const int AmountDecimals = 2;

    // numeric(14,2): doce enteros.
    public const decimal MaxAmount = 999_999_999_999.99m;

    // EF Core materializa por acá.
    private PriceListOverride()
    {
        Currency = string.Empty;
    }

    public PriceListId PriceListId { get; private set; }

    public Guid TenantId { get; private set; }

    public VariantId VariantId { get; private set; }

    public string Currency { get; private set; }

    /// <summary>IVA incluido, como el precio base. Nunca se redondea con la regla de la moneda (O-7): es el que el owner escribió.</summary>
    public decimal Amount { get; private set; }

    public static PriceListOverride Create(Guid tenantId, PriceListId priceListId, VariantId variantId, string currency, decimal amount)
    {
        EnsureAmount(amount);
        return new PriceListOverride
        {
            TenantId = tenantId,
            PriceListId = priceListId,
            VariantId = variantId,
            Currency = currency,
            Amount = amount,
        };
    }

    public void ChangeAmount(decimal amount)
    {
        EnsureAmount(amount);
        Amount = amount;
    }

    private static void EnsureAmount(decimal amount)
    {
        if (amount <= 0m || amount > MaxAmount || decimal.Round(amount, AmountDecimals) != amount)
        {
            throw new CatalogDomainException(
                "catalog.price_list.override_amount_invalid",
                "The fixed price must be greater than zero and have at most two decimals.");
        }
    }
}

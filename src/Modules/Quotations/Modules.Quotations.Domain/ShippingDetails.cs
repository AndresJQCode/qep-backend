namespace Modules.Quotations.Domain;

/// <summary>
/// La entrega por envío de una cotización o un pedido (spec 2026-10-17 flete §5.1): transportadora, cómo se
/// cobra el envío, su valor con IVA incluido, la tasa con la que se descompone y si el pedido se cobra
/// contraentrega. Transportadora y modo son independientes y opcionales (decisión 11), y la contraentrega
/// no exige transportadora (decisión 12).
///
/// Que la transportadora exista, esté activa y haga recaudo, y cuál es la tasa del tenant, lo resuelve
/// Application (<c>ShippingResolver</c>): el dominio sólo valida la coherencia (<see cref="Validated"/>).
/// </summary>
public sealed record ShippingDetails(
    Guid? CarrierId,
    string? CarrierName,
    FreightChargeMode? ChargeMode,
    decimal FreightAmount,
    int FreightTaxPercentage,
    bool CashOnDelivery)
{
    /// <summary>Sin envío registrado: lo que tiene un documento con recogida en tienda o al que nadie le
    /// cargó envío.</summary>
    public static ShippingDetails None { get; } = new(null, null, null, 0m, 0, false);

    /// <summary>
    /// Las reglas de coherencia de spec §5.2, las mismas en los dos agregados; <paramref name="errorPrefix"/>
    /// es <c>quotation.quotation</c> o <c>order.order</c>. Devuelve el envío como se guarda: fuera de COP la
    /// tasa queda en 0, sea cual sea la que llegó, porque ahí el IVA no se discrimina.
    /// </summary>
    internal ShippingDetails Validated(bool isStorePickup, string currency, string errorPrefix)
    {
        if (isStorePickup && this != None)
        {
            throw new QuotationsDomainException(
                $"{errorPrefix}.shipping_not_allowed_on_pickup",
                "A store pickup cannot carry a carrier, a freight or cash on delivery.");
        }

        var itemizesTax = QuotationItem.ItemizesTaxIn(currency);
        if (FreightAmount < 0 || (itemizesTax && FreightAmount != decimal.Truncate(FreightAmount)))
        {
            throw new QuotationsDomainException(
                $"{errorPrefix}.freight_amount_invalid",
                "The freight amount cannot be negative, and in COP it must be whole pesos.");
        }

        if (ChargeMode == FreightChargeMode.Charged && FreightAmount == 0)
        {
            throw new QuotationsDomainException(
                $"{errorPrefix}.freight_amount_required",
                "A charged freight needs an amount; a freight of zero is free.");
        }

        if ((ChargeMode is FreightChargeMode.Free or null) && FreightAmount != 0)
        {
            throw new QuotationsDomainException(
                $"{errorPrefix}.freight_amount_not_allowed",
                "A free freight, or no freight at all, cannot carry an amount.");
        }

        return itemizesTax ? this : this with { FreightTaxPercentage = 0 };
    }
}

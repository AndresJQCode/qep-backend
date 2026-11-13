using BuildingBlocks.Domain.Taxes;

namespace Modules.Quotations.Domain;

/// <summary>
/// Qué tasas de la fuente vigente entran al cálculo de un documento (spec 2026-11-10 §7.5, pasos 1–4). Es
/// una sola regla para la cotización y el pedido: la usan los dos agregados y nadie la repite.
/// </summary>
internal static class WithholdingCandidates
{
    /// <param name="source">Las tasas congeladas de la fuente vigente, en su orden.</param>
    /// <param name="billsToFinalConsumer">Consumidor final no retiene (paso 1).</param>
    /// <param name="billingAccount">La empresa facturadora; sin ella no hay país, y sin país no hay
    /// retenciones (paso 3).</param>
    /// <param name="currency">La moneda del documento: si no es la moneda en que el país discrimina
    /// impuestos, no hay base sin IVA sobre la que retener (paso 2).</param>
    public static IReadOnlyList<WithholdingRate> For(
        IEnumerable<WithholdingRate> source,
        bool billsToFinalConsumer,
        QuotationBillingAccount? billingAccount,
        string currency)
    {
        if (billsToFinalConsumer)
        {
            return [];
        }

        // Un país fuera de la tabla tampoco retiene: no se sabe en qué moneda discrimina impuestos.
        if (TaxCountries.Find(billingAccount?.Country) is not { } country
            || !string.Equals(country.TaxCurrency, currency, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // Paso 4: sólo las del país de la empresa. La foto del cliente trae las de todos sus países (D16).
        return [.. source.Where(rate => string.Equals(rate.Country, country.Country, StringComparison.OrdinalIgnoreCase))];
    }
}

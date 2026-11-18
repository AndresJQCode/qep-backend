using BuildingBlocks.Domain.Taxes;

namespace Modules.Quotations.Domain;

/// <summary>
/// Qué tasas de la fuente vigente entran al cálculo de un documento (spec 2026-11-10 §7.5, pasos 1–4) y por qué cada
/// una aplica o no (spec retenciones por UVT §5.1). Es una sola regla para la cotización y el pedido, y para «entra» y
/// «por qué no entra»: <see cref="For"/> y <see cref="Explain"/> salen del mismo <see cref="Gate"/>, así no divergen.
/// </summary>
internal static class WithholdingCandidates
{
    /// <param name="source">Las tasas congeladas de la fuente vigente, en su orden.</param>
    /// <param name="billsToFinalConsumer">Consumidor final no retiene (paso 1).</param>
    /// <param name="billingAccount">La empresa facturadora; sin ella no hay país, y sin país no hay
    /// retenciones (paso 3).</param>
    /// <param name="currency">La moneda del documento: si no es la moneda en que el país discrimina
    /// impuestos, no hay base sin IVA sobre la que retener (paso 2).</param>
    /// <param name="operationCityId">El municipio de la operación del documento: una tasa municipal aplica sólo si es
    /// de ese municipio (spec ReteICA por empresa §7.5, paso defensivo: la re-resolución ya lo garantiza).</param>
    public static IReadOnlyList<WithholdingRate> For(
        IEnumerable<WithholdingRate> source,
        bool billsToFinalConsumer,
        QuotationBillingAccount? billingAccount,
        string currency,
        Guid? operationCityId) =>
        [
            .. Gate(source, billsToFinalConsumer, billingAccount, currency, operationCityId)
                .Where(gated => gated.Blocked is null)
                .Select(gated => gated.Rate),
        ];

    /// <summary>
    /// Un veredicto por tasa de la fuente, en su orden: primero los pasos 1–4 de <see cref="For"/>, después si el
    /// documento tiene líneas del alcance de la tasa y, al final, el umbral de <see cref="DocumentTotals.ThresholdReason"/>
    /// sobre la base de ese alcance (<see cref="DocumentTotals.Evaluate"/>, la misma cuenta del total guardado). Las tasas
    /// de otro país no salen: nunca aplican a este documento (spec retenciones por UVT §5.1).
    /// </summary>
    /// <param name="bases">Las bases del documento por alcance: el subtotal sin IVA de sus líneas, sin el envío.</param>
    public static IReadOnlyList<WithholdingVerdict> Explain(
        IEnumerable<WithholdingRate> source,
        bool billsToFinalConsumer,
        QuotationBillingAccount? billingAccount,
        string currency,
        Guid? operationCityId,
        WithholdingBases bases) =>
        [
            .. Gate(source, billsToFinalConsumer, billingAccount, currency, operationCityId)
                .Select(gated => DocumentTotals.Evaluate(gated.Rate, gated.Blocked, bases)),
        ];

    // Pasos 1–4: las tasas del país de la empresa, cada una con el motivo que la deja fuera del cálculo o null si entra.
    private static IEnumerable<(WithholdingRate Rate, WithholdingReason? Blocked)> Gate(
        IEnumerable<WithholdingRate> source,
        bool billsToFinalConsumer,
        QuotationBillingAccount? billingAccount,
        string currency,
        Guid? operationCityId)
    {
        // Un país fuera de la tabla tampoco retiene: no se sabe en qué moneda discrimina impuestos.
        var country = TaxCountries.Find(billingAccount?.Country);

        // Paso 4: sólo las del país de la empresa. La foto del cliente trae las de todos sus países (D16). Sin país no
        // hay con qué filtrar, y todas quedan fuera por falta de empresa.
        var ofCountry = country is null
            ? source
            : source.Where(rate => string.Equals(rate.Country, country.Country, StringComparison.OrdinalIgnoreCase));

        return ofCountry.Select(rate => (rate, Blocked(rate)));

        WithholdingReason? Blocked(WithholdingRate rate)
        {
            if (billsToFinalConsumer)
            {
                return WithholdingReason.FinalConsumer;
            }

            if (country is null)
            {
                return WithholdingReason.NoBillingCompany;
            }

            if (!string.Equals(country.TaxCurrency, currency, StringComparison.OrdinalIgnoreCase))
            {
                return WithholdingReason.CurrencyWithoutTax;
            }

            return TaxCountries.IsMunicipal(country.Country, rate.Type) && rate.CityId != operationCityId
                ? WithholdingReason.OtherMunicipality
                : null;
        }
    }
}

using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve contra el catálogo los ids que llegan en los selectores de retención (spec impuestos por tipo §7.5, D23,
/// plan P11): cada uno existe en el tenant, está activo, retiene y es del país de la empresa facturadora, y hay uno
/// por tipo. La comparten la cotización y el pedido, cada uno con su prefijo de código. El agregado vuelve a exigir
/// país y unicidad por tipo al guardar (<c>WithholdingSelectorRules</c>); acá están las reglas que sólo se ven con
/// el catálogo en la mano.
/// </summary>
public static class WithholdingSelection
{
    /// <param name="companyCountry">El país de la empresa facturadora; null sin empresa, y entonces ningún selector
    /// sirve.</param>
    /// <param name="codePrefix"><c>quotation</c> u <c>order</c>.</param>
    /// <returns>Las fotos en el orden de <paramref name="ids"/>; vacía si no llegó ninguno.</returns>
    public static async Task<IReadOnlyList<WithholdingRate>> ResolveAsync(
        IQuotationWithholdingLookup lookup,
        Guid tenantId,
        IReadOnlyCollection<Guid> ids,
        string? companyCountry,
        DateOnly documentDate,
        string codePrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        // Sin empresa no hay país contra el que validar: ni se consulta el catálogo.
        var country = companyCountry?.Trim();
        if (string.IsNullOrEmpty(country))
        {
            throw Invalid(codePrefix);
        }

        var candidates = await lookup.ForRatesAsync(tenantId, ids, documentDate, cancellationToken);
        var rates = new List<WithholdingRate>(ids.Count);
        foreach (var id in ids)
        {
            if (!candidates.TryGetValue(id, out var candidate)
                || !candidate.IsActive
                || !candidate.Withholds
                || !string.Equals(candidate.Rate.Country, country, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(codePrefix);
            }

            rates.Add(candidate.Rate);
        }

        // El mismo id dos veces también es dos del mismo tipo.
        if (rates.GroupBy(rate => rate.Type, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new QuotationsDomainException(
                $"{codePrefix}.billing.withholding_type_repeated",
                "Only one withholding tax rate per type can be selected.");
        }

        return rates;
    }

    private static QuotationsDomainException Invalid(string codePrefix) =>
        new(
            $"{codePrefix}.billing.withholding_tax_rate_invalid",
            "Every selected withholding tax rate must exist, be active, withhold and belong to the billing company's country.");
}

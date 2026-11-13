namespace Modules.Quotations.Domain;

/// <summary>
/// Lo que el agregado vuelve a exigir a los selectores de retención (spec impuestos por tipo §7.5, D23): cada tasa
/// del país de la empresa facturadora y una por tipo. La comparten la cotización y el pedido; cada uno pone sus
/// propias guardas (parte propia, pedido pendiente) y su prefijo de código. La validación contra el catálogo vive en
/// la aplicación (<c>WithholdingSelection</c>, plan P11).
/// </summary>
internal static class WithholdingSelectorRules
{
    /// <param name="rates">Los selectores, no vacíos.</param>
    /// <param name="companyCountry">El país de la empresa facturadora; null si no hay empresa, y entonces no hay
    /// selectores posibles.</param>
    /// <param name="codePrefix"><c>quotation.billing</c> u <c>order.billing</c>.</param>
    public static void Ensure(IReadOnlyList<WithholdingRate> rates, string? companyCountry, string codePrefix)
    {
        if (companyCountry is null
            || rates.Any(rate => !string.Equals(rate.Country, companyCountry, StringComparison.OrdinalIgnoreCase)))
        {
            throw new QuotationsDomainException(
                $"{codePrefix}.withholding_tax_rate_invalid",
                "Every selected withholding tax rate must belong to the billing company's country.");
        }

        if (rates.GroupBy(rate => rate.Type, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new QuotationsDomainException(
                $"{codePrefix}.withholding_type_repeated",
                "Only one withholding tax rate per type can be selected.");
        }
    }
}

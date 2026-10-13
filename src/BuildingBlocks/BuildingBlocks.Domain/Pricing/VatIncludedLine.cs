namespace BuildingBlocks.Domain.Pricing;

/// <summary>
/// Importes de una línea con IVA incluido en el precio. <c>Subtotal</c> es la base sin IVA: es lo
/// que suma el encabezado y la base sobre la que Quotations calcula la retención en la fuente.
/// </summary>
public readonly record struct VatIncludedLineAmounts(
    decimal DiscountAmount, decimal TaxAmount, decimal Subtotal)
{
    /// <summary>Lo que se cobra por la línea, IVA adentro.</summary>
    public decimal LineTotal => Subtotal + TaxAmount;
}

/// <summary>
/// La fórmula de línea de una cotización detal, extraída de QuotationItem.Apply para que POS cobre
/// al centavo lo mismo que cotizaría (spec 2026-10-07, criterio de éxito 3). Dos copias de la
/// fórmula del IVA pueden divergir sin que ninguna prueba lo note; una sola no.
/// </summary>
public static class VatIncludedLine
{
    public static VatIncludedLineAmounts Compute(
        decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage)
    {
        // El precio se carga con IVA incluido: aquí no se suma impuesto, se extrae el que ya viene.
        var gross = quantity * unitPrice;
        var discountAmount = Round(gross * discountPercentage / 100m);

        // Lo que efectivamente se cobra por la línea, IVA adentro.
        var lineTotal = Round(gross) - discountAmount;

        // total × tasa / (100 + tasa), no total × tasa / 100: esa es la de agregar IVA a una base, y
        // sobre un precio que ya lo trae cobraría el impuesto dos veces. Con tasa 0 da 0.
        var taxAmount = Round(lineTotal * taxPercentage / (100m + taxPercentage));

        return new VatIncludedLineAmounts(discountAmount, taxAmount, lineTotal - taxAmount);
    }

    /// <summary>
    /// La línea en la moneda en que se cobra. En COP el unitario ya descontado se cobra en pesos
    /// enteros (fix 2026-10-14), y el descuento es lo que falta para llegar al bruto: la facturación
    /// masiva le manda a Siigo round("Valor Unit" × (1 + IVA)) × cantidad, y sólo con el unitario
    /// entero eso da lo mismo que se cobró. 100 kits a 35.000 con 33,33% eran 23.334,50 c/u y
    /// 2.333.450; Siigo facturaba 23.334 × 100. En otra moneda los centavos son plata y la
    /// fórmula es la de siempre.
    /// </summary>
    public static VatIncludedLineAmounts Compute(
        decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage, string currency)
    {
        if (currency != WholePesoCurrency)
        {
            return Compute(quantity, unitPrice, discountPercentage, taxPercentage);
        }

        var listPrice = ChargedListPrice(unitPrice, currency);
        var chargedUnitPrice = RoundToPeso(listPrice * (1m - discountPercentage / 100m));
        var charged = Compute(quantity, chargedUnitPrice, 0m, taxPercentage);
        return charged with { DiscountAmount = Round(quantity * listPrice) - charged.LineTotal };
    }

    /// <summary>
    /// El precio de lista con el que se cobra: en COP, en pesos enteros. Redondearlo también, y no
    /// sólo el descontado, es lo que impide que el descuento quede negativo con un precio que
    /// trae centavos.
    /// </summary>
    public static decimal ChargedListPrice(decimal unitPrice, string currency) =>
        currency == WholePesoCurrency ? RoundToPeso(unitPrice) : unitPrice;

    // Código ISO literal y no el catálogo de monedas de Tenancy: BuildingBlocks no lo referencia, y
    // la regla es de la facturación masiva en pesos, no de los decimales con que se muestra cada
    // moneda.
    private const string WholePesoCurrency = "COP";

    private static decimal RoundToPeso(decimal value) =>
        Math.Round(value, 0, MidpointRounding.AwayFromZero);

    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

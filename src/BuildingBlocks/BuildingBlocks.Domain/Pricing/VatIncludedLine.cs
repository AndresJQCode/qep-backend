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

    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

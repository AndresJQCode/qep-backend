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
/// La fórmula de línea compartida entre cotización, pedido y POS, para que POS cobre al centavo lo
/// mismo que cotizaría (spec 2026-10-07). Desde el spec 2026-10-27 el unitario va primero: se
/// redondea el unitario ya descontado con la regla de la moneda y la línea sale de unitario ×
/// cantidad, porque lo que el cliente ve, unitario × cantidad, tiene que dar la línea. El
/// descuento es lo que falta para llegar al bruto de la lista redondeada.
/// </summary>
public static class VatIncludedLine
{
    public static VatIncludedLineAmounts Compute(
        decimal quantity, decimal listPrice, decimal discountPercentage, int taxPercentage,
        PriceRounding rounding)
    {
        var charged = ChargedListPrice(listPrice, rounding);
        var unit = ChargedUnitPrice(listPrice, discountPercentage, rounding);

        // El precio se carga con IVA incluido: aquí no se suma impuesto, se extrae el que ya viene.
        var lineTotal = Round(quantity * unit);

        // Con menos decimales que el precio de lista, el bruto se calcula sobre la lista ya
        // redondeada: así el descuento nunca queda negativo.
        var discountAmount = Round(quantity * charged) - lineTotal;

        // total × tasa / (100 + tasa), no total × tasa / 100: esa es la de agregar IVA a una base, y
        // sobre un precio que ya lo trae cobraría el impuesto dos veces. Con tasa 0 da 0.
        var taxAmount = Round(lineTotal * taxPercentage / (100m + taxPercentage));

        return new VatIncludedLineAmounts(discountAmount, taxAmount, lineTotal - taxAmount);
    }

    /// <summary>El precio de lista con el que se cobra: redondeado con la regla.</summary>
    public static decimal ChargedListPrice(decimal unitPrice, PriceRounding rounding) =>
        rounding.Apply(unitPrice);

    /// <summary>
    /// El unitario ya descontado con el que se cobra: la lista redondeada por el descuento,
    /// redondeado otra vez. Es el que se multiplica por la cantidad (y el que Siigo recibe).
    /// </summary>
    public static decimal ChargedUnitPrice(
        decimal listPrice, decimal discountPercentage, PriceRounding rounding) =>
        rounding.Apply(rounding.Apply(listPrice) * (1m - discountPercentage / 100m));

    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

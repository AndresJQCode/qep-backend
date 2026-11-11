namespace Modules.Quotations.Domain;

/// <summary>Lo que una línea aporta a los totales: la foto de <c>QuotationItem</c> u <c>OrderItem</c>.</summary>
public readonly record struct LineAmountsView(decimal Subtotal, decimal DiscountAmount, decimal TaxAmount);

/// <summary>
/// Los totales de un documento (cotización o pedido) a partir de sus líneas (spec 2026-10-15 §4.3).
/// Una sola implementación para que los dos agregados sumen igual: un cambio de redondeo acá cambia
/// los dos.
///
/// Un cliente con excedente de IVA no paga IVA en el documento, cualquiera sea la tasa de cada
/// línea: el impuesto de cada línea queda intacto, pero el encabezado lo ignora entero. El IVA del
/// envío sigue la misma regla del excedente.
///
/// Las retenciones salen de las tasas congeladas del documento (spec 2026-11-10 §7.5), que llegan ya
/// filtradas por fuente, país, moneda y consumidor final. Cada una se calcula sobre el subtotal de los
/// productos, sin IVA y nunca sobre el envío, y aplica si no tiene base mínima o si el subtotal alcanza
/// su umbral. Restan del neto a cobrar, no del total: el total sigue siendo lo facturado.
/// </summary>
public readonly record struct DocumentTotals(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TaxPercentage,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal,
    decimal FreightSubtotal = 0m,
    decimal FreightTaxAmount = 0m,
    IReadOnlyList<AppliedWithholding>? Withholdings = null)
{
    /// <summary>Las tasas que aplicaron, en el orden en que llegaron. Un <c>default</c> se lee vacío.</summary>
    public IReadOnlyList<AppliedWithholding> Withholdings { get => field ?? []; init; } = Withholdings;

    /// <summary>
    /// Lo que se factura por los productos, sin el envío. El envío viaja en sus propios campos para que
    /// la pantalla pueda mostrarlo aparte sin restarlo de <see cref="Total"/>.
    /// </summary>
    public decimal ProductsTotal => Subtotal + TaxAmount;

    public static DocumentTotals Zero { get; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0m, Withholdings: []);

    public static DocumentTotals Compute(
        IEnumerable<LineAmountsView> lines,
        FreightAmounts freight,
        bool appliesVatSurplus,
        IReadOnlyList<WithholdingRate> withholdings)
    {
        var materialized = lines as IReadOnlyCollection<LineAmountsView> ?? lines.ToArray();
        var subtotal = Round(materialized.Sum(line => line.Subtotal));
        var discount = Round(materialized.Sum(line => line.DiscountAmount));
        var rawTax = Round(materialized.Sum(line => line.TaxAmount));
        var tax = appliesVatSurplus ? 0m : rawTax;
        var taxPercentage = subtotal > 0 ? Round(tax / subtotal * 100m) : 0m;
        var freightTax = appliesVatSurplus ? 0m : freight.TaxAmount;
        var total = subtotal + tax + freight.Subtotal + freightTax;
        var applied = Withhold(subtotal, withholdings);
        var retention = applied.Sum(withholding => withholding.Amount);
        return new DocumentTotals(
            subtotal, discount, tax, taxPercentage, total, retention, total - retention, freight.Subtotal, freightTax, applied);
    }

    // El umbral se compara sin redondear y es inclusivo; sólo el monto va a dos decimales (spec §7.5).
    private static AppliedWithholding[] Withhold(decimal productsSubtotal, IReadOnlyList<WithholdingRate> rates)
    {
        var applied = rates
            .Where(rate => rate.Threshold is { } threshold && productsSubtotal >= threshold)
            .Select(rate => new AppliedWithholding(rate, productsSubtotal, Round(productsSubtotal * rate.Percentage / 100m)))
            .ToArray();
        return applied.Length == 0 ? [] : applied;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

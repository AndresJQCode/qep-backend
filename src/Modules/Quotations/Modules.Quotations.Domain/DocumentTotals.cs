namespace Modules.Quotations.Domain;

/// <summary>Lo que una línea aporta a los totales: la foto de <c>QuotationItem</c> u <c>OrderItem</c>.</summary>
public readonly record struct LineAmountsView(decimal Subtotal, decimal DiscountAmount, decimal TaxAmount);

/// <summary>
/// Los totales de un documento (cotización o pedido) a partir de sus líneas (spec 2026-10-15 §4.3).
/// Una sola implementación para que los dos agregados sumen igual: un cambio de redondeo acá cambia
/// los dos.
///
/// Un cliente con excedente de IVA no paga IVA en el documento, cualquiera sea la tasa de cada
/// línea: el impuesto de cada línea queda intacto, pero el encabezado lo ignora entero. La
/// retención en la fuente es el 2,5 % de lo facturado sin IVA y resta del neto a cobrar, no del
/// total: el total sigue siendo lo facturado.
/// </summary>
public readonly record struct DocumentTotals(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal TaxPercentage,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal)
{
    public const decimal RetentionRate = 0.025m;

    public static DocumentTotals Zero { get; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0m);

    public static DocumentTotals Compute(
        IEnumerable<LineAmountsView> lines, bool appliesVatSurplus, bool appliesRetention)
    {
        var materialized = lines as IReadOnlyCollection<LineAmountsView> ?? lines.ToArray();
        var subtotal = Round(materialized.Sum(line => line.Subtotal));
        var discount = Round(materialized.Sum(line => line.DiscountAmount));
        var rawTax = Round(materialized.Sum(line => line.TaxAmount));
        var tax = appliesVatSurplus ? 0m : rawTax;
        var taxPercentage = subtotal > 0 ? Round(tax / subtotal * 100m) : 0m;
        var total = subtotal + tax;
        var retention = appliesRetention ? Round(subtotal * RetentionRate) : 0m;
        return new DocumentTotals(subtotal, discount, tax, taxPercentage, total, retention, total - retention);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

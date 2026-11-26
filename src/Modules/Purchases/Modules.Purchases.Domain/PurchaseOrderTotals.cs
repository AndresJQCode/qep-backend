namespace Modules.Purchases.Domain;

/// <summary>Totales de una orden: subtotal sin IVA, IVA y la suma de los dos.</summary>
public sealed record PurchaseOrderTotals(decimal Subtotal, decimal Tax, decimal Total);

/// <summary>
/// La única aritmética del módulo (D-19): la usan la línea, los totales de la orden y la vista previa, así que el total
/// que pinta la vista previa y el que guarda el alta no pueden divergir. Costos sin IVA; el IVA se suma encima. Cada
/// subtotal e IVA de línea se redondea a 2 decimales <c>AwayFromZero</c>, como <c>DocumentTotals</c> de cotizaciones, y
/// los totales suman esos valores ya redondeados.
/// </summary>
public static class PurchaseOrderMath
{
    private const int MoneyDecimals = 2;

    public static decimal Subtotal(decimal quantity, decimal unitCost) => Round(quantity * unitCost);

    public static decimal Tax(decimal subtotal, decimal taxRatePercent) => Round(subtotal * taxRatePercent / 100m);

    /// <summary>
    /// Precio unitario sin IVA a partir de uno con IVA incluido (D-9): la lista del mayorista lleva IVA y los costos de
    /// compras no.
    /// </summary>
    public static decimal NetOfVat(decimal grossUnitPrice, decimal taxRatePercent) =>
        Round(grossUnitPrice * 100m / (100m + taxRatePercent));

    public static PurchaseOrderTotals Sum(IEnumerable<(decimal Subtotal, decimal Tax)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var subtotal = 0m;
        var tax = 0m;
        foreach (var line in lines)
        {
            subtotal += line.Subtotal;
            tax += line.Tax;
        }

        return new PurchaseOrderTotals(subtotal, tax, subtotal + tax);
    }

    private static decimal Round(decimal value) => Math.Round(value, MoneyDecimals, MidpointRounding.AwayFromZero);
}

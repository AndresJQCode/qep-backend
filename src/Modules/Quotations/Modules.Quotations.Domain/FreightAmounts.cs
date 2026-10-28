using BuildingBlocks.Domain.Pricing;

namespace Modules.Quotations.Domain;

/// <summary>
/// Lo que el envío aporta a los totales de un documento. Sólo el modo <see cref="FreightChargeMode.Charged"/>
/// suma. En COP el valor incluye IVA y se descompone con el mismo redondeo que una línea; fuera de COP
/// no hay IVA y el valor entra entero, redondeado con la regla del documento.
/// </summary>
public readonly record struct FreightAmounts(decimal Subtotal, decimal TaxAmount)
{
    public static FreightAmounts Zero { get; } = new(0m, 0m);

    public static FreightAmounts For(FreightChargeMode? mode, decimal amount, int taxPercentage, string currency, PriceRounding rounding)
    {
        if (mode != FreightChargeMode.Charged)
        {
            return Zero;
        }

        // Fuera de COP no hay IVA que separar: la tasa se pisa en cero y el valor entra entero, pero
        // con la misma regla de redondeo que una línea del documento.
        var itemizesTax = string.Equals(currency, "COP", StringComparison.OrdinalIgnoreCase);
        var line = VatIncludedLine.Compute(1m, amount, 0m, itemizesTax ? taxPercentage : 0, rounding);
        return new FreightAmounts(line.Subtotal, line.TaxAmount);
    }
}

using BuildingBlocks.Domain.Pricing;

namespace Modules.Quotations.Domain;

/// <summary>
/// Lo que el envío aporta a los totales de un documento. Sólo el modo <see cref="FreightChargeMode.Charged"/>
/// suma. En COP el valor incluye IVA y se descompone con el mismo redondeo que una línea; fuera de COP
/// no hay IVA y el valor entra tal cual.
/// </summary>
public readonly record struct FreightAmounts(decimal Subtotal, decimal TaxAmount)
{
    public static FreightAmounts Zero { get; } = new(0m, 0m);

    public static FreightAmounts For(FreightChargeMode? mode, decimal amount, int taxPercentage, string currency)
    {
        if (mode != FreightChargeMode.Charged)
        {
            return Zero;
        }

        if (!string.Equals(currency, "COP", StringComparison.OrdinalIgnoreCase))
        {
            return new FreightAmounts(amount, 0m);
        }

        var line = VatIncludedLine.Compute(1m, amount, 0m, taxPercentage, currency);
        return new FreightAmounts(line.Subtotal, line.TaxAmount);
    }
}

using BuildingBlocks.Domain.Catalog;
using BuildingBlocks.Domain.Taxes;

namespace Modules.Quotations.Domain;

/// <summary>Lo que una línea aporta a los totales: la foto de <c>QuotationItem</c> u <c>OrderItem</c>. El tipo decide en
/// qué base de retención entra (spec 2026-11-18 §4.2).</summary>
public readonly record struct LineAmountsView(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    ProductKind Kind = ProductKind.Physical);

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
/// filtradas por fuente, país, moneda y consumidor final. Cada una se calcula sobre el subtotal sin IVA de
/// las líneas de su alcance (<see cref="WithholdingBases"/>, spec 2026-11-18 §4.2), nunca sobre el envío, y
/// aplica si no tiene base mínima o si esa base alcanza su umbral. Restan del neto a cobrar, no del total:
/// el total sigue siendo lo facturado.
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
        var bases = WithholdingBases.From(materialized);
        var subtotal = bases.All;
        var discount = Round(materialized.Sum(line => line.DiscountAmount));
        var rawTax = Round(materialized.Sum(line => line.TaxAmount));
        var tax = appliesVatSurplus ? 0m : rawTax;
        var taxPercentage = subtotal > 0 ? Round(tax / subtotal * 100m) : 0m;
        var freightTax = appliesVatSurplus ? 0m : freight.TaxAmount;
        var total = subtotal + tax + freight.Subtotal + freightTax;
        var applied = Withhold(bases, withholdings);
        var retention = applied.Sum(withholding => withholding.Amount);
        return new DocumentTotals(
            subtotal, discount, tax, taxPercentage, total, retention, total - retention, freight.Subtotal, freightTax, applied);
    }

    /// <summary>
    /// Si una tasa que ya entró al cálculo aplica por su umbral: <see cref="WithholdingReason.NoTaxUnitValue"/> sin
    /// umbral (base en unidades sin valor), <see cref="WithholdingReason.Applied"/> si el subtotal lo alcanza y
    /// <see cref="WithholdingReason.BelowMinimumBase"/> si no. El umbral se compara sin redondear y es inclusivo
    /// (spec §7.5). La usan el cálculo y el veredicto (spec retenciones por UVT §5.3).
    /// </summary>
    public static WithholdingReason ThresholdReason(WithholdingRate rate, decimal productsSubtotal) => rate.Threshold switch
    {
        null => WithholdingReason.NoTaxUnitValue,
        { } threshold when productsSubtotal >= threshold => WithholdingReason.Applied,
        _ => WithholdingReason.BelowMinimumBase,
    };

    /// <summary>El monto de una tasa que aplicó: sólo él va a dos decimales (spec §7.5).</summary>
    internal static decimal WithholdingAmount(WithholdingRate rate, decimal productsSubtotal) =>
        Round(productsSubtotal * rate.Percentage / 100m);

    /// <summary>
    /// El veredicto de una tasa que ya pasó (o no) los pasos previos al cálculo: <paramref name="blocked"/> es el motivo
    /// que la deja fuera, o null si entra. Es el único lugar donde se toma la base de una tasa, así el total guardado
    /// (<see cref="Compute"/>) y el veredicto (<c>WithholdingCandidates.Explain</c>) nunca difieren. El orden es el del
    /// spec 2026-11-18 §4.2: lo que la bloquea, después <see cref="WithholdingReason.NoLinesOfScope"/> y al final el
    /// umbral. Una tasa All con base 0 no cae en NoLinesOfScope: sigue el umbral, como antes del alcance.
    /// </summary>
    internal static WithholdingVerdict Evaluate(WithholdingRate rate, WithholdingReason? blocked, WithholdingBases bases)
    {
        var @base = bases.For(rate.AppliesTo);
        var reason = blocked
            ?? (rate.AppliesTo != WithholdingScope.All && @base == 0m
                ? WithholdingReason.NoLinesOfScope
                : ThresholdReason(rate, @base));
        var amount = reason == WithholdingReason.Applied ? WithholdingAmount(rate, @base) : 0m;
        return new WithholdingVerdict(rate, reason, @base, amount);
    }

    private static AppliedWithholding[] Withhold(WithholdingBases bases, IReadOnlyList<WithholdingRate> rates)
    {
        var applied = rates
            .Select(rate => Evaluate(rate, blocked: null, bases))
            .Where(verdict => verdict.Applies)
            .Select(verdict => new AppliedWithholding(verdict.Rate, verdict.Base, verdict.Amount))
            .ToArray();
        return applied.Length == 0 ? [] : applied;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

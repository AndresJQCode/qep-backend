using BuildingBlocks.Domain.Taxes;

namespace Modules.Quotations.Domain;

/// <summary>
/// La base de cada alcance de retención en un documento (spec 2026-11-18 §4.2): el subtotal sin IVA de todas las líneas,
/// el de los bienes y el de los servicios. El envío nunca entra. Es la única cuenta de la base: la usan el total guardado
/// (<see cref="DocumentTotals.Compute"/>), el veredicto (<c>WithholdingCandidates.Explain</c>) y el desglose
/// (<c>DocumentWithholding.Explain</c>), así la vista previa y lo guardado nunca difieren.
/// </summary>
/// <param name="All">El subtotal de todas las líneas; es el <c>Subtotal</c> del documento.</param>
/// <param name="Goods">El subtotal de las líneas <c>Physical</c>.</param>
/// <param name="Services">El subtotal de las líneas <c>Service</c>.</param>
public readonly record struct WithholdingBases(decimal All, decimal Goods, decimal Services)
{
    /// <summary>Suma cada base y la redondea igual que el subtotal del documento: a dos decimales, lejos de cero.</summary>
    public static WithholdingBases From(IEnumerable<LineAmountsView> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        decimal all = 0m, goods = 0m, services = 0m;
        foreach (var line in lines)
        {
            all += line.Subtotal;
            if (WithholdingScopes.Covers(WithholdingScope.Goods, line.Kind))
            {
                goods += line.Subtotal;
            }
            else if (WithholdingScopes.Covers(WithholdingScope.Services, line.Kind))
            {
                services += line.Subtotal;
            }
        }

        return new WithholdingBases(Round(all), Round(goods), Round(services));
    }

    /// <summary>La base de una tasa con ese alcance.</summary>
    public decimal For(WithholdingScope scope) => scope switch
    {
        WithholdingScope.All => All,
        WithholdingScope.Goods => Goods,
        WithholdingScope.Services => Services,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown withholding scope."),
    };

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

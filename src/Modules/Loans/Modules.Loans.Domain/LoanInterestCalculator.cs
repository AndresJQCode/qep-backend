namespace Modules.Loans.Domain;

public static class LoanInterestCalculator
{
    /// <summary>
    /// Parte <paramref name="from"/>..<paramref name="to"/> en tramos cada vez que cambia el capital y calcula el
    /// interés de cada uno: capital × tasa mensual × días del tramo ÷ días del período (reglas 2, 3 y 5 del spec).
    /// El cambio rige desde su día inclusive, así que el día anterior es el último con el capital viejo. Cada tramo
    /// se redondea a 2 decimales (mitad hacia arriba, no bancario) y la cuota es la suma de sus tramos, para que
    /// el detalle siempre cuadre con el total. El divisor es el período completo aunque <c>from</c> sea posterior
    /// a su inicio (regla 7). Un rango vacío (<c>from &gt; to</c>) no tiene tramos.
    /// </summary>
    public static IReadOnlyList<InterestSegment> Segments(
        LoanPeriod period,
        DateOnly from,
        DateOnly to,
        decimal openingPrincipal,
        IReadOnlyList<CapitalChange> changes,
        decimal monthlyRatePercent)
    {
        var segments = new List<InterestSegment>();
        if (from > to)
        {
            return segments;
        }

        // OrderBy es estable: con dos cambios el mismo día queda el último de la lista.
        var ordered = changes.OrderBy(change => change.EffectiveOn).ToList();

        var principal = openingPrincipal;
        foreach (var change in ordered.Where(change => change.EffectiveOn <= from))
        {
            principal = change.BalanceAfter;
        }

        var cursor = from;
        foreach (var group in ordered
                     .Where(change => change.EffectiveOn > from && change.EffectiveOn <= to)
                     .GroupBy(change => change.EffectiveOn))
        {
            AddSegment(segments, period, cursor, group.Key.AddDays(-1), principal, monthlyRatePercent);
            principal = group.Last().BalanceAfter;
            cursor = group.Key;
        }

        AddSegment(segments, period, cursor, to, principal, monthlyRatePercent);
        return segments;
    }

    public static decimal Total(IEnumerable<InterestSegment> segments) => segments.Sum(segment => segment.Interest);

    private static void AddSegment(
        List<InterestSegment> segments, LoanPeriod period, DateOnly start, DateOnly end, decimal principal, decimal ratePercent)
    {
        // Con capital en cero no hay interés que mostrar: el tramo no existe.
        if (principal == 0m)
        {
            return;
        }

        var days = end.DayNumber - start.DayNumber + 1;
        var interest = Math.Round(principal * ratePercent * days / (100m * period.Days), 2, MidpointRounding.AwayFromZero);
        segments.Add(new InterestSegment(start, end, principal, days, interest));
    }
}

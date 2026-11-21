namespace Modules.Scheduling.Domain;

/// <summary>Los rangos abiertos una fecha concreta (una franja puntual, como una jornada extendida de diciembre). <c>Date</c> es un día
/// del calendario del tenant, no un instante.</summary>
public sealed record DateHours
{
    public DateHours(DateOnly date, IEnumerable<TimeRange> ranges)
    {
        Date = date;
        Ranges = TimeRanges.Normalize(ranges);
    }

    public DateOnly Date { get; }

    /// <summary>Ordenados por inicio y sin solaparse.</summary>
    public IReadOnlyList<TimeRange> Ranges { get; }
}

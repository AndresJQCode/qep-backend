namespace Modules.Scheduling.Domain;

/// <summary>Los rangos abiertos un día de la semana, de 1 (lunes) a 7 (domingo), como los horarios de atención de Mensajería y el frontend.</summary>
public sealed record WeekdayHours
{
    public WeekdayHours(int day, IEnumerable<TimeRange> ranges)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(day, 7);

        Day = day;
        Ranges = TimeRanges.Normalize(ranges);
    }

    public int Day { get; }

    /// <summary>Ordenados por inicio y sin solaparse.</summary>
    public IReadOnlyList<TimeRange> Ranges { get; }

    /// <summary>El número de día de una fecha: lunes es 1 y domingo es 7 (<see cref="DayOfWeek"/> cuenta el domingo como 0).</summary>
    public static int DayNumber(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;
}

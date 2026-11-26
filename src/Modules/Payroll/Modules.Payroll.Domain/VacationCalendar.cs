using System.Globalization;

namespace Modules.Payroll.Domain;

/// <summary>Los días de nómina que un rango de vacaciones tiene dentro de la quincena que empieza en <paramref name="Start"/>.</summary>
public sealed record VacationQuincenaDays(DateOnly Start, DateOnly End, int Days);

/// <summary>
/// Cuenta las vacaciones con la misma regla de 30 días de <c>WorkedDaysRule</c>: una quincena entera son 15 y el 31 no cuenta. Es
/// pura (sólo fechas): los días hábiles dependen de festivos y descansos y se calculan en Application.
/// </summary>
public static class VacationCalendar
{
    private const int FullHalfDays = 15;

    /// <summary>Every quincena the range touches, in order, with its payroll days (spec «Días»).</summary>
    public static IReadOnlyList<VacationQuincenaDays> PayrollDays(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(end));
        }

        var result = new List<VacationQuincenaDays>();
        var quincena = QuincenaCalendar.Containing(start);
        while (quincena.Start <= end)
        {
            var from = start > quincena.Start ? start : quincena.Start;

            // Covering the last day of the quincena counts the rest of its 15 days (the 31st is free, February's end absorbs the
            // difference); a range that stops earlier counts its real days.
            var days = end >= quincena.End
                ? FullHalfDays - (from.DayNumber - quincena.Start.DayNumber)
                : end.DayNumber - from.DayNumber + 1;
            result.Add(new VacationQuincenaDays(quincena.Start, quincena.End, days));
            quincena = QuincenaCalendar.After(quincena.Start);
        }

        return result;
    }

    /// <summary>The «yyyy-MM» months the range touches, in order.</summary>
    public static IReadOnlyList<string> Months(DateOnly start, DateOnly end)
    {
        var months = new List<string>();
        for (var month = new DateOnly(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
        {
            months.Add(month.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        }

        return months;
    }
}

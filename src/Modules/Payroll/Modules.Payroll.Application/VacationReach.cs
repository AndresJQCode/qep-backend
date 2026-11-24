using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// Which quincenas a vacation touches (spec «Ciclo de vida y bloqueos»): the one that pays it and every quincena that reads it.
/// Pure: only dates.
/// </summary>
public static class VacationReach
{
    /// <summary>Quincena that pays a Qep vacation: the one containing StartDate (exists or not). Null for External.</summary>
    public static (DateOnly Start, DateOnly End)? PayingQuincena(Vacation vacation)
    {
        ArgumentNullException.ThrowIfNull(vacation);
        if (vacation.Origin == VacationOrigin.External)
        {
            return null;
        }

        var quincena = QuincenaCalendar.Containing(vacation.StartDate);
        return (quincena.Start, quincena.End);
    }

    /// <summary>
    /// Is this vacation part of the period's EngineInput: pays here, or has days between the 1st of the month and period.End. The
    /// second half counts the days of the first one (IBC, ARL and parafiscales of the month), so the first half is not enough.
    /// </summary>
    public static bool IsInputOf(Vacation vacation, DateOnly periodStart, DateOnly periodEnd)
    {
        ArgumentNullException.ThrowIfNull(vacation);
        return vacation.StartDate <= periodEnd && vacation.EndDate >= new DateOnly(periodStart.Year, periodStart.Month, 1);
    }

    /// <summary>
    /// Start dates of every quincena whose input includes it: the paying one, those with days, and the second half of a month whose
    /// first half has days.
    /// </summary>
    public static IReadOnlyList<DateOnly> QuincenaStarts(Vacation vacation)
    {
        ArgumentNullException.ThrowIfNull(vacation);
        var starts = new List<DateOnly>();
        for (var month = new DateOnly(vacation.StartDate.Year, vacation.StartDate.Month, 1); month <= vacation.EndDate; month = month.AddMonths(1))
        {
            foreach (var day in new[] { month, new DateOnly(month.Year, month.Month, 16) })
            {
                var quincena = QuincenaCalendar.Containing(day);
                if (IsInputOf(vacation, quincena.Start, quincena.End))
                {
                    starts.Add(quincena.Start);
                }
            }
        }

        return starts;
    }
}

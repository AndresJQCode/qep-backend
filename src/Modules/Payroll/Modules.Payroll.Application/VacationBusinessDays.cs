namespace Modules.Payroll.Application;

/// <summary>
/// Business days of an employee for vacations (CST art. 186): neither one of their rest days nor a national holiday. With Hr's default
/// (Sunday only) a Saturday is a business day. Pure: the holidays come in already resolved.
/// </summary>
public static class VacationBusinessDays
{
    public static bool IsBusinessDay(DateOnly day, IReadOnlyCollection<DayOfWeek> restDays, IReadOnlySet<DateOnly> holidays)
    {
        ArgumentNullException.ThrowIfNull(restDays);
        ArgumentNullException.ThrowIfNull(holidays);
        return !restDays.Contains(day.DayOfWeek) && !holidays.Contains(day);
    }

    public static int Count(DateOnly start, DateOnly end, IReadOnlyCollection<DayOfWeek> restDays, IReadOnlySet<DateOnly> holidays)
    {
        var count = 0;
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (IsBusinessDay(day, restDays, holidays))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>First business day after <paramref name="end"/>; holidays must cover at least end+1 .. end+31.</summary>
    public static DateOnly ReturnDate(DateOnly end, IReadOnlyCollection<DayOfWeek> restDays, IReadOnlySet<DateOnly> holidays)
    {
        // Hr requires at least one business day a week, so a month is far more than enough; the bound only guards a bad input.
        for (var offset = 1; offset <= 31; offset++)
        {
            var day = end.AddDays(offset);
            if (IsBusinessDay(day, restDays, holidays))
            {
                return day;
            }
        }

        throw new InvalidOperationException("No business day within 31 days after the end of the vacation.");
    }
}

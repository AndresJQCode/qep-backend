namespace Modules.Hr.Domain;

/// <summary>
/// Birthday matching by month and day, ignoring the year. A 29 February birthday is announced on 28 February in non-leap years
/// (and on 29 February in leap years), so it is never skipped.
/// </summary>
public static class BirthdayCalendar
{
    public static bool FallsOn(DateOnly birthDate, DateOnly day)
    {
        if (birthDate.Month == 2 && birthDate.Day == 29)
        {
            return DateTime.IsLeapYear(day.Year)
                ? day.Month == 2 && day.Day == 29
                : day.Month == 2 && day.Day == 28;
        }

        return birthDate.Month == day.Month && birthDate.Day == day.Day;
    }
}

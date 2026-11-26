using Modules.Payroll.Application;

namespace Modules.Payroll.UnitTests;

public sealed class VacationBusinessDaysTests
{
    private static readonly DayOfWeek[] SaturdayAndSunday = [DayOfWeek.Saturday, DayOfWeek.Sunday];
    private static readonly DayOfWeek[] OnlySunday = [DayOfWeek.Sunday];

    // A wide net so ReturnDate always finds its holidays: callers pass end+1 .. end+31.
    private static HashSet<DateOnly> Holidays(params DateOnly[] days) => [.. days];

    [Fact]
    public void FirstExample()
    {
        var holidays = Holidays(new DateOnly(2026, 12, 25), new DateOnly(2027, 1, 1));
        var start = new DateOnly(2026, 12, 14);
        var end = new DateOnly(2027, 1, 5);

        Assert.Equal(15, VacationBusinessDays.Count(start, end, SaturdayAndSunday, holidays));
        Assert.Equal(new DateOnly(2027, 1, 6), VacationBusinessDays.ReturnDate(end, SaturdayAndSunday, holidays));
    }

    [Fact]
    public void SecondExample()
    {
        var holidays = Holidays(new DateOnly(2027, 1, 11));
        var start = new DateOnly(2027, 1, 12);
        var end = new DateOnly(2027, 2, 1);

        Assert.Equal(15, VacationBusinessDays.Count(start, end, SaturdayAndSunday, holidays));
        Assert.Equal(new DateOnly(2027, 2, 2), VacationBusinessDays.ReturnDate(end, SaturdayAndSunday, holidays));
    }

    [Fact]
    public void SaturdayIsBusinessWithHrsDefault()
    {
        var start = new DateOnly(2026, 12, 14);
        var end = new DateOnly(2026, 12, 19);

        Assert.Equal(6, VacationBusinessDays.Count(start, end, OnlySunday, Holidays()));
        Assert.Equal(5, VacationBusinessDays.Count(start, end, SaturdayAndSunday, Holidays()));
    }

    [Fact]
    public void AHolidayOrARestDayIsNotABusinessDay()
    {
        var holidays = Holidays(new DateOnly(2026, 12, 25));

        Assert.False(VacationBusinessDays.IsBusinessDay(new DateOnly(2026, 12, 25), OnlySunday, holidays));
        Assert.False(VacationBusinessDays.IsBusinessDay(new DateOnly(2026, 12, 20), OnlySunday, holidays));
        Assert.True(VacationBusinessDays.IsBusinessDay(new DateOnly(2026, 12, 19), OnlySunday, holidays));
    }
}

using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-21 §5.2: las dos tablas están escritas a mano del calendario oficial; el cálculo tiene que producirlas.</summary>
public sealed class ColombianHolidaysTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void TwentyTwentySixHasTheEighteenOfficialDates()
    {
        DateOnly[] expected =
        [
            D(2026, 1, 1), D(2026, 1, 12), D(2026, 3, 23), D(2026, 4, 2), D(2026, 4, 3), D(2026, 5, 1), D(2026, 5, 18),
            D(2026, 6, 8), D(2026, 6, 15), D(2026, 6, 29), D(2026, 7, 20), D(2026, 8, 7), D(2026, 8, 17), D(2026, 10, 12),
            D(2026, 11, 2), D(2026, 11, 16), D(2026, 12, 8), D(2026, 12, 25),
        ];

        var actual = ColombianHolidays.For(2026);

        Assert.Equal(18, actual.Count);
        Assert.Equal(expected, actual.OrderBy(d => d));
    }

    [Fact]
    public void TwentyTwentySevenHasTheEighteenOfficialDates()
    {
        DateOnly[] expected =
        [
            D(2027, 1, 1), D(2027, 1, 11), D(2027, 3, 22), D(2027, 3, 25), D(2027, 3, 26), D(2027, 5, 1), D(2027, 5, 10),
            D(2027, 5, 31), D(2027, 6, 7), D(2027, 7, 5), D(2027, 7, 20), D(2027, 8, 7), D(2027, 8, 16), D(2027, 10, 18),
            D(2027, 11, 1), D(2027, 11, 15), D(2027, 12, 8), D(2027, 12, 25),
        ];

        var actual = ColombianHolidays.For(2027);

        Assert.Equal(18, actual.Count);
        Assert.Equal(expected, actual.OrderBy(d => d));
    }

    [Fact]
    public void AFixedHolidayOnAWeekendIsNotMoved()
    {
        Assert.Equal(DayOfWeek.Saturday, D(2027, 12, 25).DayOfWeek);
        Assert.Contains(D(2027, 12, 25), ColombianHolidays.For(2027));
    }

    [Fact]
    public void AnEmilianiHolidayAlreadyOnMondayStays()
    {
        Assert.Equal(DayOfWeek.Monday, D(2025, 1, 6).DayOfWeek);
        Assert.Contains(D(2025, 1, 6), ColombianHolidays.For(2025));
    }

    [Fact]
    public void EasterOf2026IsApril5AndDrivesFiveHolidays()
    {
        Assert.Equal(D(2026, 4, 5), ColombianHolidays.EasterSunday(2026));
        var holidays = ColombianHolidays.For(2026);
        Assert.Contains(D(2026, 4, 2), holidays);
        Assert.Contains(D(2026, 4, 3), holidays);
        Assert.Contains(D(2026, 5, 18), holidays);
        Assert.Contains(D(2026, 6, 8), holidays);
        Assert.Contains(D(2026, 6, 15), holidays);
    }

    [Fact]
    public void UpcomingCrossesTheYearBoundary()
    {
        Assert.Equal(
            [D(2026, 12, 8), D(2026, 12, 25), D(2027, 1, 1)],
            ColombianHolidays.Upcoming(D(2026, 11, 20), 3));
    }
}

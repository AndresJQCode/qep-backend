using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

/// <summary>Spec 2026-11-26 §5.2: birthday matching by month and day. Pure, no database.</summary>
public sealed class BirthdayCalendarTests
{
    [Fact]
    public void SameMonthAndDayMatches() =>
        Assert.True(BirthdayCalendar.FallsOn(new DateOnly(1990, 5, 20), new DateOnly(2026, 5, 20)));

    [Fact]
    public void OtherDayDoesNotMatch() =>
        Assert.False(BirthdayCalendar.FallsOn(new DateOnly(1990, 5, 20), new DateOnly(2026, 5, 21)));

    [Fact]
    public void Feb29IsAnnouncedOnFeb28InNonLeapYear() =>
        Assert.True(BirthdayCalendar.FallsOn(new DateOnly(2000, 2, 29), new DateOnly(2027, 2, 28)));

    [Fact]
    public void Feb29IsNotAnnouncedOnFeb28InLeapYear()
    {
        Assert.False(BirthdayCalendar.FallsOn(new DateOnly(2000, 2, 29), new DateOnly(2028, 2, 28)));
        Assert.True(BirthdayCalendar.FallsOn(new DateOnly(2000, 2, 29), new DateOnly(2028, 2, 29)));
    }

    [Fact]
    public void Dec31PlusOneIsJanFirst() =>
        Assert.True(BirthdayCalendar.FallsOn(new DateOnly(2001, 1, 1), new DateOnly(2026, 12, 31).AddDays(1)));
}

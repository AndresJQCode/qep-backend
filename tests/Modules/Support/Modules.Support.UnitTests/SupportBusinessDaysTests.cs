using System.Globalization;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Calendario: lunes a viernes con festivos 2026-11-16, 2026-12-08 y 2026-12-25; 2026-11-13 es viernes.
public sealed class SupportBusinessDaysTests
{
    private static readonly ISupportBusinessCalendar Calendar = FakeCalendar.WithHolidays;

    [Theory]
    [InlineData(15, "2026-12-07")]
    [InlineData(10, "2026-11-30")]
    [InlineData(30, "2026-12-30")]
    public void TheTermCountsFromTheNextBusinessDaySkippingHolidays(int term, string due) =>
        Assert.Equal(DateOnly.Parse(due, CultureInfo.InvariantCulture), SupportBusinessDays.Add(Calendar, new DateOnly(2026, 11, 13), term));

    [Fact]
    public void ZeroBusinessDaysIsTheSameDate() =>
        Assert.Equal(new DateOnly(2026, 11, 13), SupportBusinessDays.Add(Calendar, new DateOnly(2026, 11, 13), 0));

    [Fact]
    public void ASaturdayReceptionStartsOnTheNextBusinessDay() =>
        Assert.Equal(new DateOnly(2026, 12, 7), SupportBusinessDays.Add(Calendar, new DateOnly(2026, 11, 14), 15));

    [Fact]
    public void CountStrictlyBetweenSkipsBothEndsAndTheWeekend()
    {
        Assert.Equal(3, SupportBusinessDays.CountStrictlyBetween(Calendar, new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 23)));
        Assert.Equal(0, SupportBusinessDays.CountStrictlyBetween(Calendar, new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 17)));
    }

    [Fact]
    public void CountStrictlyBetweenIsZeroWhenToIsBeforeFrom() =>
        Assert.Equal(0, SupportBusinessDays.CountStrictlyBetween(Calendar, new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 17)));

    [Fact]
    public void AddStopsInsteadOfLoopingForeverWithoutBusinessDays()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SupportBusinessDays.Add(new NeverBusinessCalendar(), new DateOnly(2026, 11, 13), 1));

        Assert.Contains("3660", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3660, SupportBusinessDays.MaxScanDays);
    }

    [Fact]
    public void ANegativeTermIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SupportBusinessDays.Add(Calendar, new DateOnly(2026, 11, 13), -1));
}

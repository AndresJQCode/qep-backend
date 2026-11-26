using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class QuincenaCalendarTests
{
    [Fact]
    public void TheFifteenthIsInTheFirstHalf()
    {
        var (start, end, half) = QuincenaCalendar.Containing(new DateOnly(2026, 11, 15));

        Assert.Equal(new DateOnly(2026, 11, 1), start);
        Assert.Equal(new DateOnly(2026, 11, 15), end);
        Assert.Equal(PayrollHalf.First, half);
    }

    [Theory]
    [InlineData(2026, 2, 16, 2026, 2, 28)]
    [InlineData(2026, 12, 16, 2026, 12, 31)]
    [InlineData(2026, 11, 30, 2026, 11, 30)]
    public void TheSixteenthStartsTheSecondUntilTheLastDay(int year, int month, int day, int endYear, int endMonth, int endDay)
    {
        var (start, end, half) = QuincenaCalendar.Containing(new DateOnly(year, month, day));

        Assert.Equal(new DateOnly(year, month, 16), start);
        Assert.Equal(new DateOnly(endYear, endMonth, endDay), end);
        Assert.Equal(PayrollHalf.Second, half);
    }

    [Fact]
    public void AfterTheSecondComesTheFirstOfNextMonth()
    {
        var (start, end, half) = QuincenaCalendar.After(new DateOnly(2026, 12, 16));

        Assert.Equal(new DateOnly(2027, 1, 1), start);
        Assert.Equal(new DateOnly(2027, 1, 15), end);
        Assert.Equal(PayrollHalf.First, half);
    }

    [Fact]
    public void AfterTheFirstComesTheSecond()
    {
        var (start, end, half) = QuincenaCalendar.After(new DateOnly(2026, 2, 1));

        Assert.Equal(new DateOnly(2026, 2, 16), start);
        Assert.Equal(new DateOnly(2026, 2, 28), end);
        Assert.Equal(PayrollHalf.Second, half);
    }
}

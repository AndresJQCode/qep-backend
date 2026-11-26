using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class VacationCalendarTests
{
    [Fact]
    public void CrossesMonthFromTheFirstHalf()
    {
        var days = VacationCalendar.PayrollDays(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));

        Assert.Equal(
            [
                new VacationQuincenaDays(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15), 2),
                new VacationQuincenaDays(new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 31), 15),
                new VacationQuincenaDays(new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 15), 5),
            ],
            days);
    }

    [Fact]
    public void TheThirtyFirstDoesNotCount()
    {
        var days = VacationCalendar.PayrollDays(new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 31));

        Assert.Equal([new VacationQuincenaDays(new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 31), 11)], days);
    }

    [Fact]
    public void FebruaryAbsorbsTheDifference()
    {
        var days = VacationCalendar.PayrollDays(new DateOnly(2027, 2, 17), new DateOnly(2027, 3, 5));

        Assert.Equal(
            [
                new VacationQuincenaDays(new DateOnly(2027, 2, 16), new DateOnly(2027, 2, 28), 14),
                new VacationQuincenaDays(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 15), 5),
            ],
            days);
    }

    [Fact]
    public void SecondExampleOfTheSpec()
    {
        var days = VacationCalendar.PayrollDays(new DateOnly(2027, 1, 12), new DateOnly(2027, 2, 1));

        Assert.Equal(
            [
                new VacationQuincenaDays(new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 15), 4),
                new VacationQuincenaDays(new DateOnly(2027, 1, 16), new DateOnly(2027, 1, 31), 15),
                new VacationQuincenaDays(new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 15), 1),
            ],
            days);
    }

    [Fact]
    public void MonthsInOrder()
    {
        var months = VacationCalendar.Months(new DateOnly(2026, 11, 23), new DateOnly(2026, 12, 4));

        Assert.Equal(["2026-11", "2026-12"], months);
    }
}

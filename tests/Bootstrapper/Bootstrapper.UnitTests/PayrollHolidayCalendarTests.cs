namespace Bootstrapper.UnitTests;

/// <summary>Payroll is Colombian law: its holidays are always the national ones, with no look at the tenant's country.</summary>
public sealed class PayrollHolidayCalendarTests
{
    [Fact]
    public void ColombianHolidaysAcrossTheYear()
    {
        var holidays = PayrollHolidayCalendar.Between(new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 31));

        Assert.Contains(new DateOnly(2026, 12, 8), holidays);
        Assert.Contains(new DateOnly(2026, 12, 25), holidays);
        Assert.Contains(new DateOnly(2027, 1, 1), holidays);
        Assert.Contains(new DateOnly(2027, 1, 11), holidays);
        Assert.DoesNotContain(new DateOnly(2027, 1, 6), holidays);
    }
}

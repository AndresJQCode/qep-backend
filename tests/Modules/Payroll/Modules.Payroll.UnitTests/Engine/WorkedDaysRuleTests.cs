using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class WorkedDaysRuleTests
{
    private static PeriodInfo Period(int year, int month, int day)
    {
        var (start, end, half) = QuincenaCalendar.Containing(new DateOnly(year, month, day));
        return new PeriodInfo(start, end, half);
    }

    [Fact]
    public void AFullFirstHalfIsFifteen()
    {
        var worked = WorkedDaysRule.Compute(Period(2026, 11, 1), new DateOnly(2025, 1, 1), null);

        Assert.Equal(15, worked.Days);
        Assert.False(worked.FinalSettlementPending);
        Assert.Equal(new DateOnly(2026, 11, 1), worked.CoveredStart);
        Assert.Equal(new DateOnly(2026, 11, 15), worked.CoveredEnd);
    }

    [Fact]
    public void TheSecondHalfOfAThirtyOneDayMonthIsFifteenDays() =>
        Assert.Equal(15, WorkedDaysRule.Compute(Period(2026, 12, 16), new DateOnly(2025, 1, 1), null).Days);

    [Fact]
    public void TheSecondHalfOfFebruaryIsFifteen() =>
        Assert.Equal(15, WorkedDaysRule.Compute(Period(2026, 2, 16), new DateOnly(2025, 1, 1), null).Days);

    [Fact]
    public void AHireMidPeriodCountsRealDays()
    {
        var worked = WorkedDaysRule.Compute(Period(2026, 11, 1), new DateOnly(2026, 11, 10), null);

        Assert.Equal(6, worked.Days);
        Assert.Equal(new DateOnly(2026, 11, 10), worked.CoveredStart);
    }

    [Fact]
    public void AHireOnTheThirtyFirstIsOneDay() =>
        Assert.Equal(1, WorkedDaysRule.Compute(Period(2026, 12, 16), new DateOnly(2026, 12, 31), null).Days);

    [Fact]
    public void AnEndInsideThePeriodIsPendingFinalSettlement()
    {
        var worked = WorkedDaysRule.Compute(Period(2026, 11, 16), new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 20));

        Assert.Equal(5, worked.Days);
        Assert.True(worked.FinalSettlementPending);
        Assert.Equal(new DateOnly(2026, 11, 20), worked.CoveredEnd);
    }

    [Fact]
    public void AnEndOnTheLastDayIsStillFifteen()
    {
        var worked = WorkedDaysRule.Compute(Period(2026, 11, 16), new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 30));

        Assert.Equal(15, worked.Days);
        Assert.True(worked.FinalSettlementPending);
    }

    [Fact]
    public void AnEndBeforeThePeriodIsNotPending()
    {
        var worked = WorkedDaysRule.Compute(Period(2026, 11, 16), new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 10));

        Assert.Equal(0, worked.Days);
        Assert.False(worked.FinalSettlementPending);
    }
}

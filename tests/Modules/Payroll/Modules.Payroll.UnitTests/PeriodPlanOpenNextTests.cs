using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.UnitTests;

public sealed class PeriodPlanOpenNextTests
{
    private static readonly PayrollActor Ana = new(Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Ana Gomez");
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

    private static PayrollPeriod PeriodAt(DateOnly start) =>
        PayrollPeriod.Open(
            PayrollPeriodId.New(), Guid.NewGuid(), Guid.NewGuid(), QuincenaCalendar.Containing(start), Ana, T0);

    private static List<PayrollPeriod> Existing(params DateOnly[] starts) => starts.Select(PeriodAt).ToList();

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static List<DateOnly> Starts(IEnumerable<(DateOnly Start, DateOnly End, PayrollHalf Half)> plan) =>
        plan.Select(item => item.Start).ToList();

    [Fact]
    public void NoPeriodsInTheFirstHalfOpensCurrentAndNext() =>
        Assert.Equal([D(2026, 11, 1), D(2026, 11, 16)], Starts(PeriodPlan.MissingThroughNext([], D(2026, 11, 10))));

    [Fact]
    public void NoPeriodsInTheSecondHalfOpensOnlyNextMonthsFirstHalf() =>
        Assert.Equal([D(2026, 12, 1)], Starts(PeriodPlan.MissingThroughNext([], D(2026, 11, 22))));

    [Fact]
    public void ExistingUpToCurrentOpensOnlyTheNext() =>
        Assert.Equal(
            [D(2026, 11, 16)], Starts(PeriodPlan.MissingThroughNext(Existing(D(2026, 11, 1)), D(2026, 11, 10))));

    [Fact]
    public void EachCallOpensOneMoreAhead() =>
        Assert.Equal(
            [D(2026, 12, 1)],
            Starts(PeriodPlan.MissingThroughNext(Existing(D(2026, 11, 1), D(2026, 11, 16)), D(2026, 11, 10))));

    [Fact]
    public void BothHalvesOfNextMonthCanBeOpenedFromASecondHalf() =>
        Assert.Equal([D(2026, 12, 16)], Starts(PeriodPlan.MissingThroughNext(Existing(D(2026, 12, 1)), D(2026, 11, 22))));

    [Fact]
    public void NothingOpensBeyondTheEndOfNextMonth()
    {
        Assert.Empty(PeriodPlan.MissingThroughNext(Existing(D(2026, 12, 1), D(2026, 12, 16)), D(2026, 11, 22)));
        Assert.Empty(PeriodPlan.MissingThroughNext(
            Existing(D(2026, 11, 1), D(2026, 11, 16), D(2026, 12, 1), D(2026, 12, 16)), D(2026, 11, 10)));
    }

    [Fact]
    public void ExistingBehindCurrentFillsTheGapThroughTheTarget() =>
        Assert.Equal(
            [D(2026, 11, 16), D(2026, 12, 1), D(2026, 12, 16)],
            Starts(PeriodPlan.MissingThroughNext(Existing(D(2026, 11, 1)), D(2026, 12, 3))));

    [Fact]
    public void TheYearBoundaryIsCrossed() =>
        Assert.Equal(
            [D(2027, 1, 1)], Starts(PeriodPlan.MissingThroughNext(Existing(D(2026, 12, 16)), D(2026, 12, 20))));

    [Fact]
    public void NoPeriodsInDecemberSecondHalfOpensJanuaryFirst() =>
        Assert.Equal([D(2027, 1, 1)], Starts(PeriodPlan.MissingThroughNext([], D(2026, 12, 20))));

    [Fact]
    public void NextPeriodStartsOnIsTheTargetWhenItDoesNotExist() =>
        Assert.Equal(D(2026, 11, 16), PeriodPlan.NextPeriodStartsOn(Existing(D(2026, 11, 1)), D(2026, 11, 10)));

    [Fact]
    public void NextPeriodStartsOnFollowsTheLastPeriodOpenedAhead() =>
        Assert.Equal(D(2026, 12, 1), PeriodPlan.NextPeriodStartsOn(Existing(D(2026, 11, 1), D(2026, 11, 16)), D(2026, 11, 10)));

    [Fact]
    public void NextPeriodStartsOnIsNullAtTheEndOfNextMonth() =>
        Assert.Null(PeriodPlan.NextPeriodStartsOn(Existing(D(2026, 12, 1), D(2026, 12, 16)), D(2026, 11, 22)));

    [Fact]
    public void NextPeriodStartsOnFillsAGapWithTheHalfAfterToday() =>
        Assert.Equal(D(2026, 12, 16), PeriodPlan.NextPeriodStartsOn(Existing(D(2026, 11, 1)), D(2026, 12, 3)));

    [Fact]
    public void NextPeriodStartsOnWithoutPeriodsIsTheTarget() =>
        Assert.Equal(D(2026, 11, 16), PeriodPlan.NextPeriodStartsOn([], D(2026, 11, 10)));

    [Fact]
    public void CurrentPeriodIdIsThePeriodContainingToday()
    {
        var existing = Existing(D(2026, 11, 1), D(2026, 11, 16));

        Assert.Equal(existing[0].Id.Value, PeriodPlan.CurrentPeriodId(existing, D(2026, 11, 15)));
        Assert.Equal(existing[1].Id.Value, PeriodPlan.CurrentPeriodId(existing, D(2026, 11, 16)));
    }

    [Fact]
    public void CurrentPeriodIdIsNullWhenNoPeriodContainsToday()
    {
        Assert.Null(PeriodPlan.CurrentPeriodId(Existing(D(2026, 12, 1)), D(2026, 11, 22)));
        Assert.Null(PeriodPlan.CurrentPeriodId([], D(2026, 11, 22)));
    }
}

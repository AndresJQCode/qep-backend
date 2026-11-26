using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

public sealed class VacationReachTests
{
    private static Vacation Qep(DateOnly start, DateOnly end) => Vacation.Create(
        VacationId.New(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), VacationOrigin.Qep, start, end, 10, end.AddDays(1), null, [],
        new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero));

    private static Vacation External(DateOnly start, DateOnly end) => Vacation.Create(
        VacationId.New(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), VacationOrigin.External, start, end, 10, end.AddDays(1),
        2_800_000m, [], new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ThePayingQuincenaIsTheOneThatContainsTheStart()
    {
        Assert.Equal(
            (new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15)),
            VacationReach.PayingQuincena(Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5))));
        Assert.Equal(
            (new DateOnly(2027, 2, 16), new DateOnly(2027, 2, 28)),
            VacationReach.PayingQuincena(Qep(new DateOnly(2027, 2, 16), new DateOnly(2027, 3, 3))));
    }

    [Fact]
    public void AnExternalVacationHasNoPayingQuincena() =>
        Assert.Null(VacationReach.PayingQuincena(External(new DateOnly(2026, 11, 23), new DateOnly(2026, 12, 4))));

    [Fact]
    public void ItIsAnInputOfTheQuincenasFromTheStartOfTheMonthUpToItsEnd()
    {
        var vacation = Qep(new DateOnly(2026, 12, 20), new DateOnly(2027, 1, 5));

        Assert.False(VacationReach.IsInputOf(vacation, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15)));
        Assert.True(VacationReach.IsInputOf(vacation, new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 31)));
        Assert.True(VacationReach.IsInputOf(vacation, new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 15)));
        Assert.False(VacationReach.IsInputOf(vacation, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 15)));
    }

    [Fact]
    public void TheSecondHalfCountsTheDaysOfTheFirstOne() =>
        Assert.True(VacationReach.IsInputOf(
            Qep(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 10)), new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 31)));

    [Fact]
    public void QuincenaStartsListsEveryQuincenaWhoseInputIncludesIt() =>
        Assert.Equal(
            [new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 16), new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 16)],
            VacationReach.QuincenaStarts(Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5))));

    [Fact]
    public void ThePayrollDaysOfAPhotoReachTheSameQuincenasAsTheVacation()
    {
        // The photo of a paying quincena carries the payroll days per quincena, not the dates: the reach is the same.
        foreach (var (start, end) in new[]
        {
            (new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5)),
            (new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 4)),
            (new DateOnly(2026, 12, 17), new DateOnly(2026, 12, 20)),
        })
        {
            Assert.Equal(
                VacationReach.QuincenaStarts(Qep(start, end)),
                VacationReach.QuincenaStarts(VacationCalendar.PayrollDays(start, end)));
        }
    }

    [Fact]
    public void AVacationInsideTheSecondHalfOnlyReachesIt() =>
        Assert.Equal(
            [new DateOnly(2026, 12, 16)],
            VacationReach.QuincenaStarts(Qep(new DateOnly(2026, 12, 17), new DateOnly(2026, 12, 20))));
}

using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-21 (Agenda), «Franjas»: minutos locales del día en [from, to), día de la semana de 1 (lunes) a 7 (domingo), y rangos de un
// mismo día sin solaparse. Los mismos formatos de los horarios de atención de Mensajería, declarados acá porque Scheduling no puede
// depender de Messaging (D-10).
public sealed class ScheduleValuesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(480, 720)]
    [InlineData(1439, 1440)]
    [InlineData(0, 1440)]
    public void AValidRangeIsKept(int from, int to)
    {
        var range = new TimeRange(from, to);

        Assert.Equal((from, to), (range.From, range.To));
        Assert.Equal(to - from, range.Minutes);
    }

    [Theory]
    [InlineData(-1, 60)]
    [InlineData(60, 60)]
    [InlineData(120, 60)]
    [InlineData(0, 1441)]
    public void ARangeOutsideTheDayOrEmptyOrBackwardsIsRejected(int from, int to) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(from, to));

    [Fact]
    public void ARangeContainsItsStartButNotItsEnd()
    {
        var range = new TimeRange(480, 720);

        Assert.True(range.Contains(480));
        Assert.True(range.Contains(719));
        Assert.False(range.Contains(720));
        Assert.False(range.Contains(479));
    }

    [Fact]
    public void RangesThatTouchDoNotOverlapButOnesThatCrossDo()
    {
        Assert.False(new TimeRange(480, 720).Overlaps(new TimeRange(720, 900)));
        Assert.True(new TimeRange(480, 720).Overlaps(new TimeRange(700, 900)));
    }

    [Fact]
    public void TheRangesOfADayAreSortedByStart()
    {
        var day = new WeekdayHours(1, [new TimeRange(840, 1080), new TimeRange(480, 720)]);

        Assert.Equal([new TimeRange(480, 720), new TimeRange(840, 1080)], day.Ranges);
    }

    [Fact]
    public void OverlappingRangesInTheSameDayAreRejected() =>
        Assert.Throws<ArgumentException>(() => new WeekdayHours(2, [new TimeRange(480, 720), new TimeRange(700, 900)]));

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void ADayOutsideOneToSevenIsRejected(int day) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeekdayHours(day, [new TimeRange(480, 720)]));

    [Theory]
    [InlineData(2026, 11, 23, 1)]
    [InlineData(2026, 11, 24, 2)]
    [InlineData(2026, 11, 28, 6)]
    [InlineData(2026, 11, 29, 7)]
    public void TheDayOfTheWeekCountsFromMondayToSunday(int year, int month, int day, int expected) =>
        Assert.Equal(expected, WeekdayHours.DayNumber(new DateOnly(year, month, day)));

    [Fact]
    public void ADateHoursSortsAndRejectsOverlapsLikeAWeekday()
    {
        var window = new DateHours(new DateOnly(2026, 12, 19), [new TimeRange(900, 960), new TimeRange(480, 720)]);

        Assert.Equal([new TimeRange(480, 720), new TimeRange(900, 960)], window.Ranges);
        Assert.Throws<ArgumentException>(() =>
            new DateHours(new DateOnly(2026, 12, 19), [new TimeRange(480, 720), new TimeRange(600, 700)]));
    }
}

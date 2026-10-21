using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-21 §5.2: reglas del agregado y resolución abierto/cerrado (excepción, festivo, día de la semana).</summary>
public sealed class BusinessHoursScheduleTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly IReadOnlyList<DateException> NoExceptions = [];

    private static List<WeekdayHours> Week(Func<int, TimeRange[]> ranges) =>
        Enumerable.Range(1, 7).Select(d => new WeekdayHours(d, ranges(d))).ToList();

    private static List<WeekdayHours> Office() => Week(d => d <= 5 ? [new TimeRange(480, 1080)] : []);

    private static BusinessHoursSchedule Make(
        IReadOnlyList<WeekdayHours>? weekly = null,
        IReadOnlyList<DateException>? exceptions = null,
        HolidayCountry? country = null,
        string? name = "Oficina") =>
        BusinessHoursSchedule.Create(Guid.CreateVersion7(), TenantId, name, weekly ?? Office(), exceptions ?? NoExceptions, country, Today, Now);

    private static DateTimeOffset Bog(int y, int m, int d, int h, int min = 0) =>
        new(y, m, d, h, min, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void CreateTrimsTheNameAndKeepsTheSevenDaysInOrder()
    {
        var reversed = Enumerable.Reverse(Office()).ToList();

        var schedule = Make(weekly: reversed, name: "  Oficina  ");

        Assert.Equal("Oficina", schedule.Name);
        Assert.Equal(1, schedule.Version);
        Assert.False(schedule.IsDefault);
        Assert.Equal(Enumerable.Range(1, 7), schedule.Weekly.Select(w => w.Day));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnInvalidNameIsRejected(string? name)
    {
        var error = Assert.Throws<MessagingDomainException>(() => Make(name: name));
        Assert.Equal(MessagingErrorCodes.BusinessHoursNameInvalid, error.Code);
    }

    [Fact]
    public void ANameOfSixtyOneCharactersIsRejected()
    {
        var error = Assert.Throws<MessagingDomainException>(() => Make(name: new string('a', 61)));
        Assert.Equal(MessagingErrorCodes.BusinessHoursNameInvalid, error.Code);
    }

    public static TheoryData<int, int> BadRanges => new()
    {
        { -1, 600 }, { 480, 1441 }, { 600, 600 }, { 700, 600 },
    };

    [Theory]
    [MemberData(nameof(BadRanges))]
    public void InvalidRangesAreRejected(int from, int to)
    {
        var week = Week(d => d == 1 ? [new TimeRange(from, to)] : []);
        var error = Assert.Throws<MessagingDomainException>(() => Make(weekly: week));
        Assert.Equal(MessagingErrorCodes.BusinessHoursRangesInvalid, error.Code);
    }

    [Fact]
    public void OverlappingUnorderedAndTooManyRangesAreRejected()
    {
        IReadOnlyList<WeekdayHours>[] weeks =
        [
            Week(d => d == 1 ? [new TimeRange(480, 720), new TimeRange(600, 1080)] : []),
            Week(d => d == 1 ? [new TimeRange(600, 700), new TimeRange(480, 540)] : []),
            Week(d => d == 1 ? [new TimeRange(0, 60), new TimeRange(120, 180), new TimeRange(240, 300), new TimeRange(360, 420)] : []),
            Office().Take(6).ToList(),
            Office().Take(6).Append(new WeekdayHours(8, [])).ToList(),
            Office().Take(6).Append(new WeekdayHours(6, [])).ToList(),
        ];

        foreach (var week in weeks)
        {
            var error = Assert.Throws<MessagingDomainException>(() => Make(weekly: week));
            Assert.Equal(MessagingErrorCodes.BusinessHoursRangesInvalid, error.Code);
        }
    }

    [Fact]
    public void NullRangesAreRejectedWithTheirOwnCode()
    {
        var week = Week(_ => []).Select(w => w.Day == 2 ? new WeekdayHours(2, null!) : w).ToList();
        var weekError = Assert.Throws<MessagingDomainException>(() => Make(weekly: week));
        Assert.Equal(MessagingErrorCodes.BusinessHoursRangesInvalid, weekError.Code);

        var exceptionError = Assert.Throws<MessagingDomainException>(() =>
            Make(exceptions: [new DateException(new DateOnly(2026, 12, 24), null!)]));
        Assert.Equal(MessagingErrorCodes.BusinessHoursExceptionsInvalid, exceptionError.Code);
    }

    [Fact]
    public void TheAggregateCopiesTheRangesItReceives()
    {
        var ranges = new List<TimeRange> { new(480, 1080) };
        var schedule = Make(weekly: Week(d => d == 1 ? [new TimeRange(480, 1080)] : []).Select(w => w.Day == 1 ? new WeekdayHours(1, ranges) : w).ToList());

        ranges.Clear();

        Assert.Single(schedule.Weekly[0].Ranges);
    }

    [Fact]
    public void TouchingRangesAreAccepted()
    {
        var schedule = Make(weekly: Week(d => d == 1 ? [new TimeRange(480, 720), new TimeRange(720, 1080)] : []));
        Assert.Equal(2, schedule.Weekly[0].Ranges.Count);
    }

    [Fact]
    public void RepeatedExceptionDatesAreRejected()
    {
        var date = new DateOnly(2026, 12, 24);
        var error = Assert.Throws<MessagingDomainException>(() =>
            Make(exceptions: [new DateException(date, []), new DateException(date, [new TimeRange(480, 600)])]));
        Assert.Equal(MessagingErrorCodes.BusinessHoursExceptionsInvalid, error.Code);
    }

    [Fact]
    public void AnExceptionWithInvalidRangesIsRejected()
    {
        var error = Assert.Throws<MessagingDomainException>(() =>
            Make(exceptions: [new DateException(new DateOnly(2026, 12, 24), [new TimeRange(600, 600)])]));
        Assert.Equal(MessagingErrorCodes.BusinessHoursExceptionsInvalid, error.Code);
    }

    [Fact]
    public void PastExceptionsArePrunedBeforeTheCap()
    {
        var past = Enumerable.Range(1, 100).Select(i => new DateException(Today.AddDays(-i), [])).ToList();
        var future = new DateException(Today.AddDays(5), []);

        var schedule = Make(exceptions: past.Append(future).ToList());

        Assert.Single(schedule.Exceptions);
        Assert.Equal(future.Date, schedule.Exceptions[0].Date);

        var tooMany = Enumerable.Range(0, 101).Select(i => new DateException(Today.AddDays(i), [])).ToList();
        var error = Assert.Throws<MessagingDomainException>(() => Make(exceptions: tooMany));
        Assert.Equal(MessagingErrorCodes.BusinessHoursExceptionsInvalid, error.Code);
    }

    [Fact]
    public void UpdateReplacesEverythingAndBumpsTheVersion()
    {
        var schedule = Make();
        var later = Now.AddHours(1);

        schedule.Update(" Soporte ", Week(_ => [new TimeRange(0, 1440)]), [], HolidayCountry.CO, Today, later);

        Assert.Equal("Soporte", schedule.Name);
        Assert.Equal(HolidayCountry.CO, schedule.HolidayCountry);
        Assert.Equal(2, schedule.Version);
        Assert.Equal(later, schedule.UpdatedAt);
    }

    [Fact]
    public void AFailedUpdateLeavesTheScheduleAsItWas()
    {
        var schedule = Make();

        Assert.Throws<MessagingDomainException>(() => schedule.Update("Nuevo", Office().Take(3).ToList(), [], null, Today, Now));

        Assert.Equal("Oficina", schedule.Name);
        Assert.Equal(1, schedule.Version);
    }

    [Fact]
    public void MakeDefaultAndClearDefaultBumpTheVersion()
    {
        var schedule = Make();

        schedule.MakeDefault(Now);
        Assert.True(schedule.IsDefault);
        Assert.Equal(2, schedule.Version);

        schedule.ClearDefault(Now);
        Assert.False(schedule.IsDefault);
        Assert.Equal(3, schedule.Version);
    }

    [Fact]
    public void IsOpenInsideAndOutsideARange()
    {
        var schedule = Make();

        Assert.True(schedule.IsOpenAt(Bog(2026, 10, 21, 10), Bogota));
        Assert.True(schedule.IsOpenAt(new DateTimeOffset(2026, 10, 21, 15, 0, 0, TimeSpan.Zero), Bogota));
        Assert.False(schedule.IsOpenAt(Bog(2026, 10, 21, 19), Bogota));
        Assert.False(schedule.IsOpenAt(Bog(2026, 10, 21, 18), Bogota));
        Assert.False(schedule.IsOpenAt(Bog(2026, 10, 24, 10), Bogota));
    }

    [Fact]
    public void MidnightRangeIsOpenUntilTheEnd()
    {
        var schedule = Make(weekly: Week(d => d == 3 ? [new TimeRange(1320, 1440)] : []));

        Assert.True(schedule.IsOpenAt(Bog(2026, 10, 21, 23, 59), Bogota));
        Assert.False(schedule.IsOpenAt(Bog(2026, 10, 22, 0), Bogota));
    }

    [Fact]
    public void AClosedExceptionClosesAWorkingDay()
    {
        var schedule = Make(exceptions: [new DateException(Today, [])]);
        Assert.False(schedule.IsOpenAt(Bog(2026, 10, 21, 10), Bogota));
    }

    [Fact]
    public void AnExceptionWithRangesOpensOutsideTheWeek()
    {
        var sunday = new DateOnly(2026, 10, 25);
        var schedule = Make(exceptions: [new DateException(sunday, [new TimeRange(540, 780)])]);
        Assert.True(schedule.IsOpenAt(Bog(2026, 10, 25, 11), Bogota));
    }

    [Fact]
    public void AHolidayClosesTheDayUnlessAnExceptionOpensIt()
    {
        var holiday = new DateOnly(2026, 11, 2);
        var instant = Bog(2026, 11, 2, 10);

        Assert.False(Make(country: HolidayCountry.CO).IsOpenAt(instant, Bogota));
        Assert.True(Make(country: HolidayCountry.CO, exceptions: [new DateException(holiday, [new TimeRange(480, 720)])]).IsOpenAt(instant, Bogota));
        Assert.True(Make(country: null).IsOpenAt(instant, Bogota));
    }

    [Fact]
    public void NextChangeFromOpenIsTodaysClose()
    {
        var next = Make().NextChangeAt(Bog(2026, 10, 21, 10), Bogota);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 23, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextChangeFromClosedSkipsWeekendAndHoliday()
    {
        var next = Make(country: HolidayCountry.CO).NextChangeAt(Bog(2026, 10, 30, 19), Bogota);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 13, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextChangeIsNullWhenNothingChangesInTwoWeeks()
    {
        Assert.Null(Make(weekly: Week(_ => [])).NextChangeAt(Bog(2026, 10, 21, 10), Bogota));
        Assert.Null(Make(weekly: Week(_ => [new TimeRange(0, 1440)])).NextChangeAt(Bog(2026, 10, 21, 10), Bogota));
    }

    [Fact]
    public void DaylightSavingIsHonoured()
    {
        var schedule = Make(weekly: Week(_ => [new TimeRange(540, 1020)]));

        // El domingo 2026-11-01 a las 2:00 Nueva York pasa de -04:00 a -05:00.
        var nineLocal = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(-5));
        Assert.True(schedule.IsOpenAt(nineLocal, NewYork));
        Assert.False(schedule.IsOpenAt(nineLocal.AddHours(-1), NewYork));

        var before = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero), schedule.NextChangeAt(before, NewYork));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 22, 0, 0, TimeSpan.Zero), schedule.NextChangeAt(nineLocal, NewYork));
    }

    [Fact]
    public void DuplicateCopiesEverythingButDefault()
    {
        var holiday = new DateException(new DateOnly(2026, 12, 24), []);
        var schedule = Make(exceptions: [holiday], country: HolidayCountry.CO);
        schedule.MakeDefault(Now);
        var newId = Guid.CreateVersion7();

        var copy = schedule.Duplicate(newId, "Oficina (copia)", Now.AddMinutes(1));

        Assert.Equal(newId, copy.Id);
        Assert.Equal(TenantId, copy.TenantId);
        Assert.Equal("Oficina (copia)", copy.Name);
        Assert.False(copy.IsDefault);
        Assert.Equal(1, copy.Version);
        Assert.Equal(HolidayCountry.CO, copy.HolidayCountry);
        Assert.Equal(schedule.Weekly, copy.Weekly);
        Assert.Equal(schedule.Exceptions, copy.Exceptions);
    }

    [Theory]
    [InlineData("Oficina", 1, "Oficina (copia)")]
    [InlineData("Oficina", 2, "Oficina (copia 2)")]
    [InlineData("Oficina", 10, "Oficina (copia 10)")]
    public void DuplicateNameNumbers(string name, int attempt, string expected) =>
        Assert.Equal(expected, BusinessHoursSchedule.DuplicateName(name, attempt));

    [Fact]
    public void DuplicateNameTrimsTheNameAndKeepsTheSuffix()
    {
        var name = new string('a', 58);

        var names = Enumerable.Range(1, 3).Select(attempt => BusinessHoursSchedule.DuplicateName(name, attempt)).ToList();

        Assert.Equal(3, names.Distinct().Count());
        Assert.EndsWith(" (copia)", names[0]);
        Assert.EndsWith(" (copia 2)", names[1]);
        Assert.EndsWith(" (copia 3)", names[2]);
        Assert.All(names, n => Assert.True(n.Length <= 60));
    }

    [Fact]
    public void UpcomingHolidaysIsEmptyWithoutACountry() =>
        Assert.Empty(Make(country: null).UpcomingHolidays(Today));

    [Fact]
    public void UpcomingHolidaysListsTwelveMonthsWithACountry()
    {
        var list = Make(country: HolidayCountry.CO).UpcomingHolidays(new DateOnly(2026, 11, 20));

        Assert.Equal(new DateOnly(2026, 12, 8), list[0]);
        Assert.Contains(new DateOnly(2027, 1, 1), list);
        Assert.DoesNotContain(new DateOnly(2026, 11, 16), list); // antes del inicio
        Assert.Contains(new DateOnly(2027, 11, 15), list); // último festivo antes del 2027-11-20
        Assert.DoesNotContain(new DateOnly(2027, 12, 8), list); // pasados los 12 meses
        Assert.True(list.SequenceEqual(list.OrderBy(d => d)));
    }
}

using Modules.Messaging.Domain;
using Modules.Support.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El calendario de Soporte es el de la bandeja [owner 2026-11-14]: el horario predeterminado del tenant decide los días de la
/// semana, sus excepciones de fecha mandan sobre todo y los festivos son los del país del horario o, si no tiene, los del tenant.
/// Sin horario, o con un horario sin ningún día con franjas, lunes a viernes (plan D12).
/// </summary>
public sealed class SupportBusinessCalendarTests
{
    private static readonly DateOnly Friday = new(2026, 11, 13);
    private static readonly DateOnly Saturday = new(2026, 11, 14);
    private static readonly DateOnly Sunday = new(2026, 11, 15);
    private static readonly DateOnly HolidayMonday = new(2026, 11, 16);

    private static readonly TimeRange Morning = new(9 * 60, 12 * 60);

    [Fact]
    public void WithoutAScheduleMondayToFridayAreBusinessDays()
    {
        var calendar = SupportBusinessCalendar.From(null, "ES");

        Assert.True(calendar.IsBusinessDay(Friday));
        Assert.False(calendar.IsBusinessDay(Saturday));
        Assert.False(calendar.IsBusinessDay(Sunday));
    }

    [Fact]
    public void ColombiaClosesItsHolidays()
    {
        Assert.False(SupportBusinessCalendar.From(null, "CO").IsBusinessDay(HolidayMonday));
        Assert.True(SupportBusinessCalendar.From(null, "ES").IsBusinessDay(HolidayMonday));
    }

    [Fact]
    public void TheScheduleHolidayCountryWins()
    {
        var calendar = SupportBusinessCalendar.From(Schedule(MondayToFriday(), HolidayCountry.CO), "ES");

        Assert.False(calendar.IsBusinessDay(HolidayMonday));
    }

    [Fact]
    public void WithoutScheduleHolidayCountryTheTenantCountryIsUsed()
    {
        var schedule = Schedule(MondayToFriday(), holidayCountry: null);

        Assert.False(SupportBusinessCalendar.From(schedule, "CO").IsBusinessDay(HolidayMonday));
        Assert.True(SupportBusinessCalendar.From(schedule, "ES").IsBusinessDay(HolidayMonday));
    }

    [Fact]
    public void AClosingDateExceptionClosesAWeekday()
    {
        var calendar = SupportBusinessCalendar.From(
            Schedule(MondayToFriday(), HolidayCountry.CO, new DateException(new DateOnly(2026, 11, 18), [])), "CO");

        Assert.False(calendar.IsBusinessDay(new DateOnly(2026, 11, 18)));
        Assert.True(calendar.IsBusinessDay(new DateOnly(2026, 11, 19)));
    }

    [Fact]
    public void AnOpeningDateExceptionOpensTheDay()
    {
        var calendar = SupportBusinessCalendar.From(
            Schedule(MondayToFriday(), HolidayCountry.CO, new DateException(Saturday, [Morning])), "CO");

        Assert.True(calendar.IsBusinessDay(Saturday));
        Assert.False(calendar.IsBusinessDay(Sunday));
    }

    [Fact]
    public void AClosingExceptionMovesTheDeadlineOneBusinessDay()
    {
        var withoutException = SupportBusinessCalendar.From(Schedule(MondayToFriday(), HolidayCountry.CO), "CO");
        var withException = SupportBusinessCalendar.From(
            Schedule(MondayToFriday(), HolidayCountry.CO, new DateException(new DateOnly(2026, 11, 18), [])), "CO");

        Assert.Equal(new DateOnly(2026, 12, 7), SupportBusinessDays.Add(withoutException, Friday, 15));
        Assert.Equal(new DateOnly(2026, 12, 9), SupportBusinessDays.Add(withException, Friday, 15));
    }

    [Fact]
    public void TheCalendarMatchesTheInboxOnEveryDayOfAYear()
    {
        var schedule = Schedule(
            Week(open: [1, 2, 3, 4, 5, 6]),
            HolidayCountry.CO,
            new DateException(new DateOnly(2026, 3, 18), []),
            new DateException(new DateOnly(2026, 8, 9), [Morning]));
        var calendar = SupportBusinessCalendar.From(schedule, "ES");

        var mismatches = Enumerable.Range(0, 365)
            .Select(offset => new DateOnly(2026, 1, 1).AddDays(offset))
            .Where(day => calendar.IsBusinessDay(day) != schedule.RangesFor(day).Count > 0)
            .ToList();

        Assert.Empty(mismatches);
        // Cubre los tres caminos de la regla: la excepción que cierra, la que abre un domingo y un festivo en día hábil.
        Assert.False(calendar.IsBusinessDay(new DateOnly(2026, 3, 18)));
        Assert.True(calendar.IsBusinessDay(new DateOnly(2026, 8, 9)));
        Assert.False(calendar.IsBusinessDay(new DateOnly(2026, 12, 8)));
    }

    [Fact]
    public void TheScheduleDecidesTheWeekdays()
    {
        var calendar = SupportBusinessCalendar.From(Schedule(Week(open: [1, 2, 3, 4, 5, 6]), HolidayCountry.CO), "CO");

        Assert.True(calendar.IsBusinessDay(Saturday));
        Assert.False(calendar.IsBusinessDay(Sunday));
    }

    [Fact]
    public void AScheduleWithEveryDayClosedFallsBackToMondayToFriday()
    {
        var calendar = SupportBusinessCalendar.From(Schedule(Week(open: []), HolidayCountry.CO), "CO");

        Assert.True(calendar.IsBusinessDay(Friday));
        Assert.False(calendar.IsBusinessDay(Saturday));
        Assert.False(calendar.IsBusinessDay(HolidayMonday));
        Assert.Equal(new DateOnly(2026, 12, 7), SupportBusinessDays.Add(calendar, Friday, 15));
    }

    private static List<WeekdayHours> MondayToFriday() => Week(open: [1, 2, 3, 4, 5]);

    private static List<WeekdayHours> Week(int[] open) =>
        Enumerable.Range(1, 7)
            .Select(day => new WeekdayHours(day, open.Contains(day) ? [Morning] : []))
            .ToList();

    // «Hoy» el 1 de enero: el horario descarta las excepciones pasadas al crearse.
    private static BusinessHoursSchedule Schedule(
        IReadOnlyList<WeekdayHours> weekly, HolidayCountry? holidayCountry, params DateException[] exceptions) =>
        BusinessHoursSchedule.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Principal",
            weekly,
            exceptions,
            holidayCountry,
            new DateOnly(2026, 1, 1),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
}

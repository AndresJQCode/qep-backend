using Modules.Messaging.Domain;
using Modules.Support.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Los términos de Soporte cuentan de lunes a viernes [owner 2026-11-15, spec §13.4], no con el horario de atención del tenant: en
/// un tenant de Colombia se descuentan los festivos nacionales (<see cref="ColombianHolidays"/>); en cualquier otro país, sólo
/// lunes a viernes.
/// </summary>
public sealed class SupportBusinessCalendarTests
{
    private static readonly DateOnly Friday = new(2026, 11, 13);
    private static readonly DateOnly Saturday = new(2026, 11, 14);
    private static readonly DateOnly Sunday = new(2026, 11, 15);
    private static readonly DateOnly HolidayMonday = new(2026, 11, 16);

    [Theory]
    [InlineData("CO")]
    [InlineData("ES")]
    public void MondayToFridayAreBusinessDaysAndTheWeekendIsNot(string country)
    {
        var calendar = SupportBusinessCalendar.From(country);

        Assert.True(calendar.IsBusinessDay(Friday));
        Assert.False(calendar.IsBusinessDay(Saturday));
        Assert.False(calendar.IsBusinessDay(Sunday));
    }

    [Fact]
    public void ColombiaClosesItsHolidaysAndOtherCountriesDoNot()
    {
        Assert.False(SupportBusinessCalendar.From("CO").IsBusinessDay(HolidayMonday));
        Assert.False(SupportBusinessCalendar.From(" co ").IsBusinessDay(HolidayMonday));
        Assert.True(SupportBusinessCalendar.From("ES").IsBusinessDay(HolidayMonday));
    }

    [Fact]
    public void TheDeadlineSkipsTheWeekendAndColombianHolidays()
    {
        Assert.Equal(new DateOnly(2026, 12, 7), SupportBusinessDays.Add(SupportBusinessCalendar.From("CO"), Friday, 15));
        Assert.Equal(new DateOnly(2026, 12, 4), SupportBusinessDays.Add(SupportBusinessCalendar.From("ES"), Friday, 15));
    }

    // Reemplaza a la prueba que amarraba el calendario al de la bandeja: la regla ya no depende del horario.
    [Fact]
    public void TheCalendarIsMondayToFridayLessColombianHolidaysOnEveryDayOfAYear()
    {
        var colombia = SupportBusinessCalendar.From("CO");
        var spain = SupportBusinessCalendar.From("ES");
        var year = Enumerable.Range(0, 365).Select(offset => new DateOnly(2026, 1, 1).AddDays(offset)).ToList();

        var mismatches = year
            .Where(day =>
            {
                var weekday = day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
                return colombia.IsBusinessDay(day) != (weekday && !ColombianHolidays.For(2026).Contains(day))
                    || spain.IsBusinessDay(day) != weekday;
            })
            .ToList();

        Assert.Empty(mismatches);
        // 2026 tiene 261 días de lunes a viernes, y sus 18 festivos colombianos caen todos entre semana.
        Assert.Equal(261, year.Count(spain.IsBusinessDay));
        Assert.Equal(243, year.Count(colombia.IsBusinessDay));
    }
}

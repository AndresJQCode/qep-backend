using Modules.Tenancy.Application;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// El día de negocio es el día local del tenant (spec 2026-09-17, decisión 1). Todas las fronteras
/// salen del mismo instante de ejemplo: el 31 de diciembre de 2026 a las 23:00 en Bogotá, que en
/// UTC ya es 2027.
/// </summary>
public sealed class TenantCalendarTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private static readonly DateTimeOffset NewYearsEveInBogota = new(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TodayIsTheLocalDateOnNewYearsEve()
    {
        var calendar = new TenantCalendar(NewYearsEveInBogota, Bogota);

        Assert.Equal(new DateOnly(2026, 12, 31), calendar.Today);
    }

    // 00:30 UTC son las 19:30 del día anterior en Bogotá: la frontera diaria que hoy mueve todo.
    [Fact]
    public void TodayStaysOnTheLocalDayAfterSevenInTheEvening()
    {
        var calendar = new TenantCalendar(new DateTimeOffset(2026, 9, 17, 0, 30, 0, TimeSpan.Zero), Bogota);

        Assert.Equal(new DateOnly(2026, 9, 16), calendar.Today);
    }

    // Llegue con el offset que llegue, UtcNow es el mismo instante expresado en UTC.
    [Fact]
    public void UtcNowIsTheSameInstantWithAZeroOffset()
    {
        var calendar = new TenantCalendar(
            new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.FromHours(-5)), Bogota);

        Assert.Equal(NewYearsEveInBogota, calendar.UtcNow);
        Assert.Equal(TimeSpan.Zero, calendar.UtcNow.Offset);
        Assert.Same(Bogota, calendar.TimeZone);
    }

    [Fact]
    public void ToLocalShowsTheInstantOnTheTenantsWallClock()
    {
        var calendar = new TenantCalendar(NewYearsEveInBogota, Bogota);

        var local = calendar.ToLocal(NewYearsEveInBogota);

        Assert.Equal(new DateTime(2026, 12, 31, 23, 0, 0), local.DateTime);
        Assert.Equal(TimeSpan.FromHours(-5), local.Offset);
    }

    [Fact]
    public void StartOfDayIsTheLocalMidnightAsAnUtcInstant()
    {
        var calendar = new TenantCalendar(NewYearsEveInBogota, Bogota);

        var start = calendar.StartOfDayUtc(new DateOnly(2026, 12, 31));

        Assert.Equal(new DateTimeOffset(2026, 12, 31, 5, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.Zero, start.Offset);
    }

    [Fact]
    public void EndOfDayExclusiveIsTheNextLocalMidnight()
    {
        var calendar = new TenantCalendar(NewYearsEveInBogota, Bogota);

        Assert.Equal(
            new DateTimeOffset(2027, 1, 1, 5, 0, 0, TimeSpan.Zero),
            calendar.EndOfDayExclusiveUtc(new DateOnly(2026, 12, 31)));
    }

    // Nueva York adelanta el reloj a las 02:00 del 8 de marzo de 2026: ese día dura 23 horas, y el
    // tipo no puede suponer que todos duran 24 aunque Colombia no tenga horario de verano.
    [Fact]
    public void ADayThatSpringsForwardLastsTwentyThreeHours()
    {
        var calendar = new TenantCalendar(
            NewYearsEveInBogota, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var day = new DateOnly(2026, 3, 8);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), calendar.StartOfDayUtc(day));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), calendar.EndOfDayExclusiveUtc(day));
    }

    // Chile adelanta el reloj a la medianoche del 6 de septiembre de 2026: las 00:00 locales no
    // existen, y el día empieza en el primer instante válido, las 01:00 (-03:00).
    [Fact]
    public void AMidnightInsideTheSpringForwardGapStartsAtTheFirstValidInstant()
    {
        var calendar = new TenantCalendar(
            NewYearsEveInBogota, TimeZoneInfo.FindSystemTimeZoneById("America/Santiago"));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero),
            calendar.StartOfDayUtc(new DateOnly(2026, 9, 6)));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero),
            calendar.EndOfDayExclusiveUtc(new DateOnly(2026, 9, 5)));
    }

    // Azores atrasa el reloj de 01:00 a 00:00 el 25 de octubre de 2026: la medianoche ocurre dos
    // veces, y el día empieza en la primera (offset 0), no en la segunda (-01:00).
    [Fact]
    public void AnAmbiguousMidnightStartsAtItsFirstOccurrence()
    {
        var calendar = new TenantCalendar(
            NewYearsEveInBogota, TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Azores"));

        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero),
            calendar.StartOfDayUtc(new DateOnly(2026, 10, 25)));
    }
}

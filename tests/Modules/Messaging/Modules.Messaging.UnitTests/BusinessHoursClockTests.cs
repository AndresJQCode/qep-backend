using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-28 «Business-seconds clock»: segundos de atención entre dos instantes bajo un horario, en la zona del tenant.</summary>
public sealed class BusinessHoursClockTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    private static List<WeekdayHours> Week(Func<int, TimeRange[]> ranges) =>
        Enumerable.Range(1, 7).Select(d => new WeekdayHours(d, ranges(d))).ToList();

    // Lunes a viernes 08:00-12:00 y 14:00-18:00.
    private static BusinessHoursSchedule Office(IReadOnlyList<DateException>? exceptions = null, HolidayCountry? country = null) =>
        BusinessHoursSchedule.Create(
            Guid.CreateVersion7(), TenantId, "Oficina",
            Week(d => d <= 5 ? [new TimeRange(480, 720), new TimeRange(840, 1080)] : []),
            exceptions ?? [], country, Today, Now);

    private static DateTimeOffset Bog(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void WithoutAScheduleItIsWallClock()
    {
        var seconds = BusinessHoursClock.BusinessSeconds(null, Bog(2026, 10, 3, 3), Bog(2026, 10, 3, 4, 30), Bogota);
        Assert.Equal(5400, seconds);
    }

    [Fact]
    public void ZeroOrNegativeIntervalsAreZero()
    {
        var at = Bog(2026, 10, 5, 9);
        Assert.Equal(0, BusinessHoursClock.BusinessSeconds(Office(), at, at, Bogota));
        Assert.Equal(0, BusinessHoursClock.BusinessSeconds(Office(), at.AddHours(1), at, Bogota));
        Assert.Equal(0, BusinessHoursClock.BusinessSeconds(null, at.AddHours(1), at, Bogota));
    }

    [Fact]
    public void WithinASingleRangeItIsTheElapsedTime()
    {
        // Lunes 5 de octubre de 2026.
        Assert.Equal(1800, BusinessHoursClock.BusinessSeconds(Office(), Bog(2026, 10, 5, 9), Bog(2026, 10, 5, 9, 30), Bogota));
    }

    [Fact]
    public void ArrivingOutOfHoursAndAnsweredBeforeOpeningIsZero()
    {
        // Lunes 06:00 → 07:59: aún cerrado.
        Assert.Equal(0, BusinessHoursClock.BusinessSeconds(Office(), Bog(2026, 10, 5, 6), Bog(2026, 10, 5, 7, 59), Bogota));
    }

    [Fact]
    public void ArrivingOutOfHoursCountsFromOpening()
    {
        Assert.Equal(600, BusinessHoursClock.BusinessSeconds(Office(), Bog(2026, 10, 5, 6), Bog(2026, 10, 5, 8, 10), Bogota));
    }

    [Fact]
    public void TheLunchGapDoesNotCount()
    {
        // 11:00 → 15:00 = 1 h (11-12) + 1 h (14-15).
        Assert.Equal(7200, BusinessHoursClock.BusinessSeconds(Office(), Bog(2026, 10, 5, 11), Bog(2026, 10, 5, 15), Bogota));
    }

    [Fact]
    public void SpansNightAndWeekend()
    {
        // Viernes 17:00 → lunes 09:00 = 1 h del viernes + 1 h del lunes.
        Assert.Equal(7200, BusinessHoursClock.BusinessSeconds(Office(), Bog(2026, 10, 2, 17), Bog(2026, 10, 5, 9), Bogota));
    }

    [Fact]
    public void AnExceptionReplacesTheWeekday()
    {
        // Lunes 5 cerrado por excepción; martes 6 normal.
        var closed = Office([new DateException(new DateOnly(2026, 10, 5), [])]);
        Assert.Equal(3600, BusinessHoursClock.BusinessSeconds(closed, Bog(2026, 10, 5, 10), Bog(2026, 10, 6, 9), Bogota));
    }

    [Fact]
    public void AColombianHolidayIsClosedAllDay()
    {
        // Lunes 12 de octubre de 2026: Día de la Raza (festivo trasladado).
        var withHolidays = Office(country: HolidayCountry.CO);
        Assert.Equal(0, BusinessHoursClock.BusinessSeconds(withHolidays, Bog(2026, 10, 12, 9), Bog(2026, 10, 12, 17), Bogota));
        Assert.Equal(7200, BusinessHoursClock.BusinessSeconds(Office(), Bog(2026, 10, 12, 9), Bog(2026, 10, 12, 11), Bogota));
    }

    [Fact]
    public void ADayWithoutRangesAddsNothing()
    {
        // Sábado completo.
        Assert.Equal(0, BusinessHoursClock.BusinessSeconds(Office(), Bog(2026, 10, 3, 0), Bog(2026, 10, 3, 23), Bogota));
    }

    [Fact]
    public void ConvertsInstantsThroughTheTenantZone()
    {
        // 15:00 UTC del lunes = 10:00 en Bogotá → dentro de 08-12.
        var from = new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(1800, BusinessHoursClock.BusinessSeconds(Office(), from, from.AddMinutes(30), Bogota));
    }

    [Fact]
    public void HandlesTheSpringForwardGapInAZoneWithDst()
    {
        // Nueva York, domingo 8 de marzo de 2026: 02:00→03:00 no existe. Horario del domingo 01:00-04:00 (3 h nominales, 2 reales).
        var sunday = BusinessHoursSchedule.Create(
            Guid.CreateVersion7(), TenantId, "Dom", Week(d => d == 7 ? [new TimeRange(60, 240)] : []), [], null, new DateOnly(2026, 3, 1), Now);
        var from = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5));
        var to = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(2 * 3600, BusinessHoursClock.BusinessSeconds(sunday, from, to, NewYork));
    }

    [Fact]
    public void HandlesTheFallBackRepeatedHourInAZoneWithDst()
    {
        // Nueva York, domingo 1 de noviembre de 2026: la 01:00-02:00 ocurre dos veces. Horario 00:00-04:00 = 5 h reales.
        var sunday = BusinessHoursSchedule.Create(
            Guid.CreateVersion7(), TenantId, "Dom", Week(d => d == 7 ? [new TimeRange(0, 240)] : []), [], null, new DateOnly(2026, 10, 1), Now);
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));
        var to = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(5 * 3600, BusinessHoursClock.BusinessSeconds(sunday, from, to, NewYork));
    }
}

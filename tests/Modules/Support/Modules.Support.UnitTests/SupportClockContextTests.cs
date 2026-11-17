using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

public sealed class SupportClockContextTests
{
    // Review Focus 2: 03:30Z del viernes 13 son las 22:30 del jueves 12 en Bogotá (UTC-5).
    [Fact]
    public void TheLocalDateIsTheTenantsNotUtcs()
    {
        var clock = new SupportClockContext(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

        Assert.Equal(new DateOnly(2026, 11, 12), clock.LocalDate(new DateTimeOffset(2026, 11, 13, 3, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 11, 13), clock.LocalDate(new DateTimeOffset(2026, 11, 13, 5, 0, 0, TimeSpan.Zero)));
    }
}

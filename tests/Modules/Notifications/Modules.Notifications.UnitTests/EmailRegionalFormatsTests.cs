using Modules.Notifications.Infrastructure.Messaging;
using Modules.Tenancy.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>De la configuración regional del tenant al formato del correo (spec 2026-10-23, D-R2).</summary>
public sealed class EmailRegionalFormatsTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private static readonly DateTimeOffset Instant = new(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);

    private static TenantRegionalSettings Settings(string dateFormat, string numberFormat) =>
        new("America/Bogota", dateFormat, "COP", numberFormat);

    [Fact]
    public void UsesTheTenantDateAndNumberFormat()
    {
        var format = EmailRegionalFormats.For(Bogota, Settings("MM/dd/yyyy", "1,234.56"));

        Assert.Equal("12/31/2026 23:00", format.Expiry(Instant));
        Assert.Equal("1,234", format.Count(1234));
    }

    [Fact]
    public void WithoutARegionalRowIsColombian()
    {
        var format = EmailRegionalFormats.For(Bogota, null);

        Assert.Equal("31/12/2026 23:00", format.Expiry(Instant));
        Assert.Equal("1.234", format.Count(1234));
    }

    [Fact]
    public void AnUnknownDateFormatFallsBackToDayFirst()
    {
        var format = EmailRegionalFormats.For(Bogota, Settings("dd.MM.yy", "1.234,56"));

        Assert.Equal("31/12/2026 23:00", format.Expiry(Instant));
    }

    [Fact]
    public void AnUnknownNumberFormatFallsBackToColombian()
    {
        var format = EmailRegionalFormats.For(Bogota, Settings("dd/MM/yyyy", "x"));

        Assert.Equal("1.234", format.Count(1234));
    }

    [Fact]
    public void KeepsTheCalendarTimeZone()
    {
        var format = EmailRegionalFormats.For(Bogota, Settings("yyyy-MM-dd", "1,234.56"));

        Assert.Same(Bogota, format.TimeZone);
    }
}

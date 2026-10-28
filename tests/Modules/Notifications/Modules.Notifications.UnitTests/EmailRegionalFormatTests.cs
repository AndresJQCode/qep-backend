using System.Globalization;
using Modules.Notifications.Application;
using Modules.Tenancy.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>El formato regional de los correos de exportación (spec 2026-10-23, D-R2).</summary>
public sealed class EmailRegionalFormatTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    [Fact]
    public void ColombianIsDayFirstWithDotGrouping()
    {
        var format = EmailRegionalFormat.Colombian(Bogota);

        Assert.Equal("31/12/2026 23:00", format.Expiry(new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero)));
        Assert.Equal("1.234", format.Count(1234));
    }

    // Application no ve Tenancy y por eso copia el valor por defecto: esta prueba, que sí lo ve,
    // amarra la copia.
    [Fact]
    public void ColombianMatchesTheTenancyFallback()
    {
        var format = EmailRegionalFormat.Colombian(Bogota);
        var fallback = RegionalNumberFormat.ToNumberFormatInfo(null);

        Assert.Equal(RegionalDateFormat.Pattern(null), format.DateFormat);
        Assert.Equal(fallback.NumberGroupSeparator, format.Numbers.NumberGroupSeparator);
        Assert.Equal(fallback.NumberDecimalSeparator, format.Numbers.NumberDecimalSeparator);
    }

    // La `/` del patrón es el separador de fecha de la cultura: con de-DE saldría `.` si el formato
    // usara la cultura del proceso.
    [Fact]
    public void TheSlashStaysASlashUnderAnotherProcessCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var format = new EmailRegionalFormat(
                Bogota, "MM/dd/yyyy", RegionalNumberFormat.ToNumberFormatInfo("1,234.56"));

            Assert.Equal(
                "12/31/2026 23:00",
                format.Expiry(new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

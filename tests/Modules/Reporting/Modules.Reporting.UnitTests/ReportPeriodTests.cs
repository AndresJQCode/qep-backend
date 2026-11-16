using Modules.Reporting.Application;
using Modules.Tenancy.Application;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// Spec 2026-11-16 §5: el reporte de pedidos corta por la fecha del pedido, que es un día y no un instante. El período
/// lleva los dos días tal cual llegaron, además de los instantes que siguen usando las ventas POS y los demás reportes.
/// </summary>
public sealed class ReportPeriodTests
{
    private static readonly TenantCalendar Calendar = new(
        new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    [Fact]
    public void ThePeriodCarriesTheDaysNextToTheInstants()
    {
        var period = ReportPeriod.Of(Calendar, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.Equal(((DateOnly?)new DateOnly(2026, 10, 1), (DateOnly?)new DateOnly(2026, 10, 31)), (period.From, period.To));
        Assert.Equal(
            (new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero)),
            (period.Start!.Value, period.EndExclusive!.Value));
    }

    [Fact]
    public void AnOpenPeriodHasNoDays()
    {
        var period = ReportPeriod.Of(Calendar, null, null);

        Assert.Equal(((DateOnly?)null, (DateOnly?)null), (period.From, period.To));
    }
}

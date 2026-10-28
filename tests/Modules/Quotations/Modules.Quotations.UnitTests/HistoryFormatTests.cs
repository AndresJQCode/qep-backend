using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-23 D-R6: el historial nuevo imprime fechas y números con el formato del tenant, y sin
/// fila cae en el colombiano.</summary>
public sealed class HistoryFormatTests
{
    private static HistoryFormat With(string dateFormat, string numberFormat) =>
        HistoryFormat.For(new TenantRegionalSettings("America/Bogota", dateFormat, "COP", numberFormat));

    [Fact]
    public void NumbersKeepUpToTwoDecimalsWithGrouping()
    {
        var colombian = HistoryFormat.For(null);
        Assert.Equal("1.500", colombian.Number(1500m));
        Assert.Equal("1,5", colombian.Number(1.5m));
        Assert.Equal("1.234,5", colombian.Number(1234.5m));
        Assert.Equal("0,5", colombian.Number(0.5m));
        Assert.Equal("3", colombian.Number(3.00m));
        Assert.Equal("2,46", colombian.Number(2.456m));

        var dot = With(TenantDateFormats.DayMonthYear, TenantNumberFormats.DotDecimal);
        Assert.Equal("1,500", dot.Number(1500m));
        Assert.Equal("1.5", dot.Number(1.5m));
        Assert.Equal("1,234.5", dot.Number(1234.5m));
    }

    [Fact]
    public void DatesFollowTheTenantFormat()
    {
        var date = new DateOnly(2026, 12, 31);

        Assert.Equal("31/12/2026", HistoryFormat.For(null).Date(date));
        Assert.Equal("31/12/2026", With("x", TenantNumberFormats.CommaDecimal).Date(date));
        Assert.Equal("12/31/2026", With(TenantDateFormats.MonthDayYear, TenantNumberFormats.CommaDecimal).Date(date));
        Assert.Equal("2026-12-31", With(TenantDateFormats.IsoDate, TenantNumberFormats.CommaDecimal).Date(date));
    }
}

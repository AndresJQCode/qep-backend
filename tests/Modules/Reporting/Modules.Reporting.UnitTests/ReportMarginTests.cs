using Modules.Reporting.Application;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El margen % del reporte (spec 2026-11-10 §6.3): utilidad ÷ venta neta × 100, a un decimal con el
/// redondeo de <c>round()</c> de PostgreSQL (lejos del cero), y nulo sin venta neta.
/// </summary>
public sealed class ReportMarginTests
{
    [Theory]
    [InlineData(200000, 80000, 40.0)]
    [InlineData(30000, 10000, 33.3)]
    [InlineData(3, 2, 66.7)]
    [InlineData(1000, -250, -25.0)]
    public void ThePercentIsRoundedToOneDecimal(int netSales, int grossProfit, double expected)
    {
        Assert.Equal((decimal)expected, ReportMargin.Percent(netSales, grossProfit));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -500)]
    public void WithoutNetSalesThereIsNoPercent(int netSales, int grossProfit)
    {
        Assert.Null(ReportMargin.Percent(netSales, grossProfit));
    }
}

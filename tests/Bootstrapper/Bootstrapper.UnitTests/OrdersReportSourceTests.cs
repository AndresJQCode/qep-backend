using Modules.Quotations.Domain;
using Modules.Reporting.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El filtro «origen» de los reportes se redeclara en Reporting (no referencia Quotations): cada valor tiene que llegar al
/// <see cref="OrderSource"/> que filtra. Un origen nuevo sin mapeo tumba el reporte con una excepción.
/// </summary>
public sealed class OrdersReportSourceTests
{
    [Fact]
    public void WholesaleFilterMapsToTheWholesaleSource()
    {
        Assert.Equal(OrderSource.Wholesale, OrdersReportSource.MapSource(OrderSourceFilter.Wholesale));
        Assert.Equal(OrderSource.Wholesale, MarginReportSource.MapSource(OrderSourceFilter.Wholesale));
    }

    [Fact]
    public void EveryFilterMapsWithoutThrowing()
    {
        foreach (var filter in Enum.GetValues<OrderSourceFilter>())
        {
            var orders = OrdersReportSource.MapSource(filter);
            var margin = MarginReportSource.MapSource(filter);
            Assert.Equal(orders, margin);
        }
    }
}

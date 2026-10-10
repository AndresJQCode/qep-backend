using ClosedXML.Excel;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure.Excel;
using Modules.Tenancy.Application;

namespace Modules.Catalog.UnitTests;

public sealed class ClosedXmlProductExportBuilderTests
{
    private static readonly CurrencyInfo[] CopAndUsd = [Currencies.Get("COP"), Currencies.Get("USD")];

    [Fact]
    public void ColumnsFollowTheCurrenciesInUseAndEachScaleCarriesItsDiscountAndFinals()
    {
        var bytes = new ClosedXmlProductExportBuilder().Build(
            [
                Row("A-1", new() { ["COP"] = 45_000m, ["USD"] = 12.5m }, new ProductExportScale(1, 9, 10m)),
                Row("B-2", new() { ["COP"] = 100_000m }, new ProductExportScale(10, 19, 5m)),
            ],
            CopAndUsd);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("Productos");

        Assert.Equal(
            [
                "Codigo", "Nombre", "Descripcion", "Estado", "Tasa de impuesto",
                "Precio base COP", "Precio base USD",
                "Descuento 1-9 (%)", "Final 1-9 COP", "Final 1-9 USD",
                "Descuento 10-19 (%)", "Final 10-19 COP", "Final 10-19 USD",
            ],
            sheet.Row(1).CellsUsed().Select(cell => cell.GetString()).ToArray());

        // A: both prices, the 1-9 finals derived per currency, nothing in 10-19.
        Assert.Equal(45_000m, sheet.Cell(2, 6).GetValue<decimal>());
        Assert.Equal("#,##0", sheet.Cell(2, 6).Style.NumberFormat.Format);
        Assert.Equal(12.5m, sheet.Cell(2, 7).GetValue<decimal>());
        Assert.Equal("#,##0.00", sheet.Cell(2, 7).Style.NumberFormat.Format);
        Assert.Equal(10m, sheet.Cell(2, 8).GetValue<decimal>());
        Assert.Equal(40_500m, sheet.Cell(2, 9).GetValue<decimal>());
        Assert.Equal(11.25m, sheet.Cell(2, 10).GetValue<decimal>());
        Assert.True(sheet.Cell(2, 11).IsEmpty());

        // B: no USD price, so no USD base and no USD final — empty, never 0.
        Assert.True(sheet.Cell(3, 7).IsEmpty());
        Assert.Equal(5m, sheet.Cell(3, 11).GetValue<decimal>());
        Assert.Equal(95_000m, sheet.Cell(3, 12).GetValue<decimal>());
        Assert.True(sheet.Cell(3, 13).IsEmpty());
    }

    private static ProductExportRow Row(string code, Dictionary<string, decimal> prices, params ProductExportScale[] scales) =>
        new(code, "Producto " + code, null, true, prices, "IVA 19%", scales);
}

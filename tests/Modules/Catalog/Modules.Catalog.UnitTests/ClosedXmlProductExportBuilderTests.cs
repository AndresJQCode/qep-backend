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
                "Código", "Código de barras", "Producto", "Variante", "Descripción", "Estado", "Tasa de impuesto",
                "Precio base COP", "Precio base USD",
                "Descuento 1-9 (%)", "Final 1-9 COP", "Final 1-9 USD",
                "Descuento 10-19 (%)", "Final 10-19 COP", "Final 10-19 USD",
            ],
            sheet.Row(1).CellsUsed().Select(cell => cell.GetString()).ToArray());

        // A: both prices, the 1-9 finals derived per currency, nothing in 10-19.
        Assert.Equal(45_000m, sheet.Cell(2, 8).GetValue<decimal>());
        Assert.Equal("#,##0", sheet.Cell(2, 8).Style.NumberFormat.Format);
        Assert.Equal(12.5m, sheet.Cell(2, 9).GetValue<decimal>());
        Assert.Equal("#,##0.00", sheet.Cell(2, 9).Style.NumberFormat.Format);
        Assert.Equal(10m, sheet.Cell(2, 10).GetValue<decimal>());
        Assert.Equal(40_500m, sheet.Cell(2, 11).GetValue<decimal>());
        Assert.Equal(11.25m, sheet.Cell(2, 12).GetValue<decimal>());
        Assert.True(sheet.Cell(2, 13).IsEmpty());

        // B: no USD price, so no USD base and no USD final — empty, never 0.
        Assert.True(sheet.Cell(3, 9).IsEmpty());
        Assert.Equal(5m, sheet.Cell(3, 13).GetValue<decimal>());
        Assert.Equal(95_000m, sheet.Cell(3, 14).GetValue<decimal>());
        Assert.True(sheet.Cell(3, 15).IsEmpty());
    }

    // Variantes, Tarea 8: cada fila es una variante. El producto repite sus datos en cada una, la
    // variante por defecto deja Variante vacia, un codigo de barras ausente deja la celda vacia, y el
    // estado es el de la fila (la variante inactiva sale "Inactivo" aunque el producto este activo).
    [Fact]
    public void WritesOneRowPerVariantRepeatingTheProductAndLeavingTheDefaultVariantNameEmpty()
    {
        var bytes = new ClosedXmlProductExportBuilder().Build(
            [
                new ProductExportRow(
                    "CAM-S", "7701234567890", "Camiseta", "Camiseta – S", "Algodón", true,
                    new Dictionary<string, decimal> { ["COP"] = 40_000m }, "IVA 19%", []),
                new ProductExportRow(
                    "CAM-M", null, "Camiseta", "Camiseta – M", "Algodón", false,
                    new Dictionary<string, decimal> { ["COP"] = 45_000m }, "IVA 19%", []),
                new ProductExportRow(
                    "VS-001", null, "Vela", null, null, true,
                    new Dictionary<string, decimal> { ["COP"] = 12_000m }, null, []),
            ],
            [Currencies.Get("COP")]);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("Productos");

        string[] RowOf(int row) =>
            Enumerable.Range(1, 7).Select(column => sheet.Cell(row, column).GetString()).ToArray();

        Assert.Equal(["CAM-S", "7701234567890", "Camiseta", "Camiseta – S", "Algodón", "Activo", "IVA 19%"], RowOf(2));
        Assert.Equal(["CAM-M", "", "Camiseta", "Camiseta – M", "Algodón", "Inactivo", "IVA 19%"], RowOf(3));
        Assert.Equal(["VS-001", "", "Vela", "", "", "Activo", ""], RowOf(4));
        Assert.Equal(40_000m, sheet.Cell(2, 8).GetValue<decimal>());
        Assert.Equal(45_000m, sheet.Cell(3, 8).GetValue<decimal>());
        Assert.Equal(12_000m, sheet.Cell(4, 8).GetValue<decimal>());
        Assert.Equal(4, sheet.LastRowUsed()!.RowNumber());
    }

    private static ProductExportRow Row(string code, Dictionary<string, decimal> prices, params ProductExportScale[] scales) =>
        new(code, null, "Producto " + code, null, null, true, prices, "IVA 19%", scales);
}

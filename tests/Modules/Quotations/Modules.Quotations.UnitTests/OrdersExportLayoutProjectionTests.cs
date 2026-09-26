using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Cómo sale la celda de una columna fija (ajuste 2026-09-26). El owner vio el "0.19" de una fija
/// llegar como texto: en un Excel con configuración regional colombiana un número se muestra
/// "0,19", pero un texto se queda "0.19" y el ERP no lo suma. Una fija cuyo valor es un número
/// canónico en cultura invariante sale como número; el resto, como el texto que el tenant escribió.
/// </summary>
public sealed class OrdersExportLayoutProjectionTests
{
    [Theory]
    [InlineData("0.19", "0.19")]
    [InlineData("9999", "9999")]
    [InlineData("-1", "-1")]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("901851609", "901851609")]
    public void AFixedValueThatIsACanonicalInvariantNumberIsWrittenAsANumber(string value, string expected)
    {
        var cell = FixedCell(value);

        Assert.Equal(ExportCell.OfNumber(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)), cell);
    }

    // Lo que no es un número canónico queda como lo escribió el tenant: un cero a la izquierda es un
    // código ("02"), una coma decimal es de una cultura que no es la del archivo, y un signo "+" o
    // un espacio dicen que alguien lo escribió a propósito así.
    [Theory]
    [InlineData("02")]
    [InlineData("0000-00-00")]
    [InlineData("PM")]
    [InlineData("")]
    [InlineData("1,5")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1.")]
    [InlineData(".5")]
    public void AnyOtherFixedValueStaysText(string value)
    {
        // Directo contra la regla y no a través de un layout: OrdersExportColumnSetting.Fixed
        // recorta los extremos, así que " 1" nunca llega hasta acá por ese camino.
        Assert.Equal(ExportCell.OfText(value), OrdersExportLayoutProjection.FixedCellFor(value));
    }

    [Fact]
    public void AnEmptyFixedValueFromALayoutStaysAnEmptyTextCell()
    {
        Assert.Equal(ExportCell.OfText(string.Empty), FixedCell("   "));
    }

    private static ExportCell FixedCell(string value)
    {
        var projection = OrdersExportLayoutProjection.For(
            [OrdersExportColumnSetting.Fixed("Fija", value, visible: true)]);

        return Assert.Single(projection.Project([]));
    }
}

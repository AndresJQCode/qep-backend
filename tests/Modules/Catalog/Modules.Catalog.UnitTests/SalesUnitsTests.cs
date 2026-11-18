using BuildingBlocks.Domain.Catalog;

namespace Modules.Catalog.UnitTests;

/// <summary>Spec §3.2: cada unidad de venta lleva su código UN/ECE Rec. 20. Vive en Catalog.UnitTests porque
/// BuildingBlocks no tiene proyecto de pruebas propio.</summary>
public sealed class SalesUnitsTests
{
    [Theory]
    [InlineData(SalesUnit.Unit, "94")]
    [InlineData(SalesUnit.Service, "E48")]
    [InlineData(SalesUnit.Hour, "HUR")]
    [InlineData(SalesUnit.Day, "DAY")]
    [InlineData(SalesUnit.Week, "WEE")]
    [InlineData(SalesUnit.Month, "MON")]
    [InlineData(SalesUnit.Year, "ANN")]
    [InlineData(SalesUnit.Kilogram, "KGM")]
    [InlineData(SalesUnit.Meter, "MTR")]
    [InlineData(SalesUnit.SquareMeter, "MTK")]
    [InlineData(SalesUnit.Liter, "LTR")]
    public void EveryUnitHasItsRec20Code(SalesUnit unit, string code)
    {
        Assert.Equal(code, SalesUnits.CodeOf(unit));
    }

    [Fact]
    public void EverySalesUnitHasACode()
    {
        foreach (var unit in Enum.GetValues<SalesUnit>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SalesUnits.CodeOf(unit)));
        }
    }
}

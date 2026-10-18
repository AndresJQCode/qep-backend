using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-10-15 §6.2.1 paso 5 (H10): Geography no tiene el código ISO 3166-2, así que el módulo trae la tabla. Los
/// nombres son los de la semilla de Geography (<c>departments.json</c>); que cada uno resuelva contra la base lo prueba
/// <c>ColombiaProvinceCodesGeographyTests</c>.</summary>
public sealed class ColombiaProvinceCodesTests
{
    [Fact]
    public void ColombiaProvinceCodesHas33Entries()
    {
        Assert.Equal(33, ColombiaProvinceCodes.All.Count);
        Assert.Equal(33, ColombiaProvinceCodes.All.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("BOGOTÁ, D.C.", ColombiaProvinceCodes.DepartmentNameFor("DC"));
        Assert.Equal("ANTIOQUIA", ColombiaProvinceCodes.DepartmentNameFor("ANT"));
        Assert.Equal("ARCHIPIÉLAGO DE SAN ANDRÉS, PROVIDENCIA Y SANTA CATALINA", ColombiaProvinceCodes.DepartmentNameFor("SAP"));
    }

    [Theory]
    [InlineData("ant", "ANTIOQUIA")]
    [InlineData(" vac ", "VALLE DEL CAUCA")]
    [InlineData("CO-CUN", "CUNDINAMARCA")]
    public void DepartmentNameForIgnoresCaseSpacesAndTheCountryPrefix(string code, string expected) =>
        Assert.Equal(expected, ColombiaProvinceCodes.DepartmentNameFor(code));

    [Theory]
    [InlineData("XX")]
    [InlineData("")]
    [InlineData("   ")]
    public void DepartmentNameForUnknownCodeIsNull(string code) =>
        Assert.Null(ColombiaProvinceCodes.DepartmentNameFor(code));
}

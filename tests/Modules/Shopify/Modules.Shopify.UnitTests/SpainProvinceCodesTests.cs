using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>La tabla INE → ISO 3166-2 de España. Que cubra las regiones sembradas lo prueba
/// <c>SpainProvinceCodesGeographyTests</c> contra la base; que Shopify use estos códigos se confirma antes del merge.</summary>
public sealed class SpainProvinceCodesTests
{
    [Fact]
    public void EveryIneCodeHasAnIsoCode()
    {
        Assert.Equal(52, SpainProvinceCodes.All.Count);
        Assert.Equal(
            Enumerable.Range(1, 52).Select(code => code.ToString("00", System.Globalization.CultureInfo.InvariantCulture)),
            SpainProvinceCodes.All.Keys.Order(StringComparer.Ordinal));
        Assert.All(SpainProvinceCodes.All.Values, iso => Assert.False(string.IsNullOrWhiteSpace(iso)));
        Assert.Equal(52, SpainProvinceCodes.All.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("28", "M")]
    [InlineData(" 28 ", "M")]
    [InlineData("08", "B")]
    [InlineData("07", "PM")]
    [InlineData("35", "GC")]
    [InlineData("38", "TF")]
    [InlineData("51", "CE")]
    [InlineData("52", "ML")]
    [InlineData("53", null)]
    [InlineData("", null)]
    public void IsoForTranslatesTheIneCode(string ineCode, string? iso) =>
        Assert.Equal(iso, SpainProvinceCodes.IsoFor(ineCode));

    [Theory]
    [InlineData("M", "28")]
    [InlineData(" m ", "28")]
    [InlineData("B", "08")]
    [InlineData("CO", "14")]
    [InlineData("XX", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void IneForTranslatesTheIsoCodeBack(string? iso, string? ine) => Assert.Equal(ine, SpainProvinceCodes.IneFor(iso));

    [Fact]
    public void IneForInvertsEveryEntry() =>
        Assert.All(SpainProvinceCodes.All, entry => Assert.Equal(entry.Key, SpainProvinceCodes.IneFor(entry.Value)));
}

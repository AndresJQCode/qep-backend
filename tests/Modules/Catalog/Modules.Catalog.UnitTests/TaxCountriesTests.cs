using BuildingBlocks.Domain.Taxes;

namespace Modules.Catalog.UnitTests;

/// <summary>Spec §7.1: la tabla en código es la única definición de tipos y unidades por país.
/// Vive en Catalog.UnitTests porque BuildingBlocks no tiene proyecto de pruebas propio.</summary>
public sealed class TaxCountriesTests
{
    [Fact]
    public void ColombiaHasItsThreeTypes()
    {
        var co = TaxCountries.Colombia;

        Assert.Equal("CO", co.Country);
        Assert.Equal("UVT", co.TaxUnitName);
        Assert.Equal("COP", co.TaxCurrency);
        Assert.Equal(["IVA", "Retefuente", "ReteICA"], co.Types.Select(t => t.Type));

        var iva = co.Types[0];
        Assert.Equal(TaxBehavior.Adds, iva.Behavior);
        Assert.False(iva.AllowsFraction);
        Assert.False(iva.AllowsMinimumBase);

        foreach (var withheld in co.Types.Skip(1))
        {
            Assert.Equal(TaxBehavior.Withholds, withheld.Behavior);
            Assert.True(withheld.AllowsFraction);
            Assert.True(withheld.AllowsMinimumBase);
        }
    }

    [Fact]
    public void FindIsCaseInsensitiveOnCountry() =>
        Assert.Same(TaxCountries.Colombia, TaxCountries.Find(" co "));

    [Fact]
    public void UnknownCountryIsNull()
    {
        Assert.Null(TaxCountries.Find("ES"));
        Assert.Null(TaxCountries.Find(null));
    }

    [Fact]
    public void FindTypeIsExact()
    {
        Assert.Null(TaxCountries.FindType("CO", "reteica"));
        Assert.NotNull(TaxCountries.FindType("CO", "ReteICA"));
    }

    [Fact]
    public void TypeOfAnotherCountryIsNull() =>
        Assert.Null(TaxCountries.FindType("ES", "IVA"));
}

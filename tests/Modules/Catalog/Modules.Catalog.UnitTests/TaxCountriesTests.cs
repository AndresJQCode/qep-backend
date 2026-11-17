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

    // Spec ReteICA por empresa §7.1: el alcance territorial decide las reglas, nunca el nombre del tipo.
    [Fact]
    public void ColombiaScopes()
    {
        Assert.Equal(TaxScope.National, TaxCountries.FindType("CO", "IVA")!.Scope);
        Assert.Equal(TaxScope.National, TaxCountries.FindType("CO", "Retefuente")!.Scope);
        Assert.Equal(TaxScope.Municipal, TaxCountries.FindType("CO", "ReteICA")!.Scope);
    }

    [Fact]
    public void MunicipalTypesOfColombiaIsReteIca() =>
        Assert.Equal(["ReteICA"], TaxCountries.MunicipalTypes(" co ").Select(t => t.Type));

    [Fact]
    public void MunicipalTypesOfUnknownCountryIsEmpty()
    {
        Assert.Empty(TaxCountries.MunicipalTypes("ES"));
        Assert.Empty(TaxCountries.MunicipalTypes(null));
    }

    [Fact]
    public void IsMunicipal()
    {
        Assert.True(TaxCountries.IsMunicipal("CO", "ReteICA"));
        Assert.False(TaxCountries.IsMunicipal("CO", "Retefuente"));
        Assert.False(TaxCountries.IsMunicipal("ES", "ReteICA"));
    }
}

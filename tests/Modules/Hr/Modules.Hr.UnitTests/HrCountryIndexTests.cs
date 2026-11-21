using Modules.Hr.Application;

namespace Modules.Hr.UnitTests;

public sealed class HrCountryIndexTests
{
    private static readonly HrCountryIndex Index = new(
    [
        new HrCountryRef("CO", "Colombia"),
        new HrCountryRef("PE", "Perú"),
        new HrCountryRef("VE", "Venezuela"),
    ]);

    [Fact]
    public void ACodeIsResolvedAnyCase()
    {
        Assert.Equal("CO", Index.FindByCodeOrName("CO")?.Code);
        Assert.Equal("CO", Index.FindByCodeOrName(" co ")?.Code);
        Assert.True(Index.IsKnown("VE"));
        Assert.False(Index.IsKnown("ve"));
        Assert.False(Index.IsKnown("XX"));
    }

    [Fact]
    public void ASpanishNameIsResolvedWithoutCaseAccentsOrExtraSpaces()
    {
        Assert.Equal("PE", Index.FindByCodeOrName("peru")?.Code);
        Assert.Equal("PE", Index.FindByCodeOrName("  PERÚ ")?.Code);
        Assert.Equal("VE", Index.FindByCodeOrName("venezuela")?.Code);
    }

    [Fact]
    public void AnUnknownTextIsNull() => Assert.Null(Index.FindByCodeOrName("Narnia"));

    [Fact]
    public void NamesListsTheCatalogForTheValidValuesSheet() =>
        Assert.Equal(["Colombia", "Perú", "Venezuela"], Index.Names);

    // El índice de las pruebas del alta: cuatro países de prueba, no el catálogo completo de Geografía.
    private static readonly HrCountryIndex FourCountries = new(
    [
        new HrCountryRef("CO", "Colombia"),
        new HrCountryRef("PE", "Perú"),
        new HrCountryRef("VE", "Venezuela"),
        new HrCountryRef("US", "Estados Unidos"),
    ]);

    [Fact]
    public void ACodeOutsideTheCatalogIsUnknown()
    {
        // Tiene forma de país, pero no está en la lista de Geografía.
        Assert.False(FourCountries.IsKnown("DE"));
        Assert.True(FourCountries.IsKnown("US"));
    }

    [Fact]
    public void NamesMatchWithoutAccents()
    {
        Assert.Equal("PE", FourCountries.FindByCodeOrName("peru")?.Code);
        Assert.Equal("PE", FourCountries.FindByCodeOrName(" PERÚ ")?.Code);
        Assert.Equal("PE", FourCountries.FindByCodeOrName("pe")?.Code);
        Assert.Equal("US", FourCountries.FindByCodeOrName("estados unidos")?.Code);
    }

    [Fact]
    public void NamesAreListedForTheTemplate() =>
        Assert.Equal(["Colombia", "Perú", "Venezuela", "Estados Unidos"], FourCountries.Names);
}

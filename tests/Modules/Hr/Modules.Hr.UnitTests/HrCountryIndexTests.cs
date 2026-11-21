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
}

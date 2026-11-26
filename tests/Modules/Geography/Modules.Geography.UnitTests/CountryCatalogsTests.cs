using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class CountryCatalogsTests
{
    [Fact]
    public void FindIsCaseAndSpaceInsensitive()
    {
        var catalog = CountryCatalogs.Find(" es ");

        Assert.NotNull(catalog);
        Assert.Equal("ES", catalog.Country);
        Assert.Equal(RegionKind.Province, catalog.RegionKind);
    }

    [Fact]
    public void ColombiaIsDepartment()
    {
        var catalog = CountryCatalogs.Find("CO");

        Assert.NotNull(catalog);
        Assert.Equal(RegionKind.Department, catalog.RegionKind);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("")]
    [InlineData(null)]
    public void FindReturnsNullForACountryWithoutCatalog(string? country)
    {
        Assert.Null(CountryCatalogs.Find(country));
    }

    [Fact]
    public void AllListsColombiaThenSpain()
    {
        Assert.Equal(
            [("CO", RegionKind.Department), ("ES", RegionKind.Province)],
            CountryCatalogs.All.Select(catalog => (catalog.Country, catalog.RegionKind)));
    }
}

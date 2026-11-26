using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class RegionTests
{
    [Fact]
    public void CreateTrimsCodeAndName()
    {
        var region = Region.Create(RegionId.New(), "ES", " 28 ", "  Madrid ");

        Assert.Equal("ES", region.CountryCode);
        Assert.Equal("28", region.Code);
        Assert.Equal("Madrid", region.Name);
    }

    [Theory]
    [InlineData("ESP")]
    [InlineData("e1")]
    [InlineData("es")]
    [InlineData("")]
    public void CreateRejectsACountryThatIsNotTwoUppercaseLetters(string country)
    {
        var exception = Assert.Throws<GeographyDomainException>(
            () => Region.Create(RegionId.New(), country, "28", "Madrid"));

        Assert.Equal("geography.region.country_invalid", exception.Code);
    }

    [Fact]
    public void CreateRejectsABlankCode()
    {
        var exception = Assert.Throws<GeographyDomainException>(
            () => Region.Create(RegionId.New(), "ES", " ", "Madrid"));

        Assert.Equal("geography.region.code_required", exception.Code);
    }

    [Fact]
    public void CreateRejectsABlankName()
    {
        var exception = Assert.Throws<GeographyDomainException>(
            () => Region.Create(RegionId.New(), "ES", "28", " "));

        Assert.Equal("geography.region.name_required", exception.Code);
    }

    [Fact]
    public void RenameRejectsABlankName()
    {
        var region = Region.Create(RegionId.New(), "ES", "28", "Madrid");

        Assert.Throws<GeographyDomainException>(() => region.Rename(" "));
    }

    [Fact]
    public void RenameTrimsTheName()
    {
        var region = Region.Create(RegionId.New(), "ES", "28", "Madrid");

        region.Rename(" Madrid, Comunidad ");

        Assert.Equal("Madrid, Comunidad", region.Name);
    }

    [Fact]
    public void ARegionCityRejectsBlankCodeAndName()
    {
        var regionId = RegionId.New();

        Assert.Equal(
            "geography.region_city.code_required",
            Assert.Throws<GeographyDomainException>(
                () => RegionCity.Create(RegionCityId.New(), regionId, " ", "Madrid")).Code);
        Assert.Equal(
            "geography.region_city.name_required",
            Assert.Throws<GeographyDomainException>(
                () => RegionCity.Create(RegionCityId.New(), regionId, "28079", " ")).Code);
    }

    [Fact]
    public void ARegionCityTrimsAndRenames()
    {
        var regionId = RegionId.New();
        var city = RegionCity.Create(RegionCityId.New(), regionId, " 28079 ", " Madrid ");

        Assert.Equal(regionId, city.RegionId);
        Assert.Equal("28079", city.Code);
        Assert.Equal("Madrid", city.Name);

        city.Rename(" Madrid capital ");

        Assert.Equal("Madrid capital", city.Name);
        Assert.Throws<GeographyDomainException>(() => city.Rename(""));
    }
}

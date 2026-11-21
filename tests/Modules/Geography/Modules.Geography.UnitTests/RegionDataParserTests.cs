using System.Text;
using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.UnitTests;

public sealed class RegionDataParserTests
{
    [Fact]
    public void ParsesRegionsAndCities()
    {
        var regions = RegionDataParser.ParseRegions(ToStream(
            """[{ "code": "28", "name": "Madrid" }, { "code": "08", "name": "Barcelona" }]"""));
        var cities = RegionDataParser.ParseCities(ToStream(
            """[{ "code": "28079", "name": "Madrid", "regionCode": "28" }]"""));

        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, region => region.Code == "08" && region.Name == "Barcelona");
        Assert.Equal(new RegionCityRecord("28079", "Madrid", "28"), Assert.Single(cities));
    }

    [Fact]
    public void ADuplicateRegionCodeThrows()
    {
        Assert.Throws<InvalidOperationException>(() => RegionDataParser.ParseRegions(ToStream(
            """[{ "code": "28", "name": "Madrid" }, { "code": "28", "name": "Otra" }]""")));
    }

    [Fact]
    public void ADuplicateCityCodeThrows()
    {
        Assert.Throws<InvalidOperationException>(() => RegionDataParser.ParseCities(ToStream(
            """
            [{ "code": "28079", "name": "Madrid", "regionCode": "28" },
             { "code": "28079", "name": "Otra", "regionCode": "28" }]
            """)));
    }

    [Fact]
    public void ABlankNameThrows()
    {
        Assert.Throws<InvalidOperationException>(() => RegionDataParser.ParseRegions(ToStream(
            """[{ "code": "28", "name": " " }]""")));
    }

    [Fact]
    public void ACityWhoseCodeDoesNotStartWithItsRegionThrows()
    {
        Assert.Throws<InvalidOperationException>(() => RegionDataParser.ParseCities(ToStream(
            """[{ "code": "08019", "name": "Barcelona", "regionCode": "28" }]""")));
    }

    [Fact]
    public void TheEmbeddedSpainFilesAreConsistent()
    {
        var regions = RegionDataParser.ParseRegions(GeographySeeder.OpenResource("Seed.Data.es-regions.json"));
        var cities = RegionDataParser.ParseCities(GeographySeeder.OpenResource("Seed.Data.es-cities.json"));

        Assert.Equal(52, regions.Count);
        var regionCodes = regions.Select(region => region.Code).ToHashSet();
        Assert.All(cities, city => Assert.Contains(city.RegionCode, regionCodes));
        Assert.Contains(cities, city => city is { Code: "28079", Name: "Madrid", RegionCode: "28" });
        Assert.Contains(cities, city => city is { Code: "08019", Name: "Barcelona", RegionCode: "08" });
        Assert.Equal(8132, cities.Count);
    }

    private static MemoryStream ToStream(string json) => new(Encoding.UTF8.GetBytes(json));
}

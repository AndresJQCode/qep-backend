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

    [Fact]
    public void ValidateRejectsACityWhoseRegionWasNotSeeded()
    {
        var regions = RegionDataParser.ParseRegions(ToStream("""[{ "code": "28", "name": "Madrid" }]"""));
        var cities = RegionDataParser.ParseCities(ToStream(
            """[{ "code": "99001", "name": "Fantasma", "regionCode": "99" }]"""));

        Assert.Throws<InvalidOperationException>(() => RegionDataParser.Validate(regions, cities));
    }

    [Fact]
    public void ValidateAcceptsConsistentData()
    {
        var regions = RegionDataParser.ParseRegions(ToStream("""[{ "code": "28", "name": "Madrid" }]"""));
        var cities = RegionDataParser.ParseCities(ToStream(
            """[{ "code": "28079", "name": "Madrid", "regionCode": "28" }]"""));

        RegionDataParser.Validate(regions, cities);
    }

    // El INE escribe el artículo al final ("Rozas de Madrid, Las"); el selector y lo que se guarda como texto
    // llevan el nombre como se conoce ("Las Rozas de Madrid").
    [Fact]
    public void TheEmbeddedSpainNamesDoNotKeepTheInvertedArticle()
    {
        var regions = RegionDataParser.ParseRegions(GeographySeeder.OpenResource("Seed.Data.es-regions.json"));
        var cities = RegionDataParser.ParseCities(GeographySeeder.OpenResource("Seed.Data.es-cities.json"));
        var inverted = new System.Text.RegularExpressions.Regex(
            @", (la|las|el|los|a|o|les|l'|els|es|as|sa|ses|os|illes)(/|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.DoesNotContain(regions, region => inverted.IsMatch(region.Name));
        Assert.DoesNotContain(cities, city => inverted.IsMatch(city.Name));
        Assert.Contains(regions, region => region.Name == "A Coruña");
        Assert.Contains(regions, region => region.Name == "Las Palmas");
        Assert.Contains(cities, city => city.Name == "Las Rozas de Madrid");
        Assert.Contains(cities, city => city.Name == "L'Hospitalet de Llobregat");
    }

    private static MemoryStream ToStream(string json) => new(Encoding.UTF8.GetBytes(json));
}

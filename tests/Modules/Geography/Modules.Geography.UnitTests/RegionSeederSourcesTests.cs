using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.UnitTests;

/// <summary>Las dos listas de países (<see cref="CountryCatalogs.All"/> y las fuentes del seeder) se escriben a mano: esto las ata.</summary>
public sealed class RegionSeederSourcesTests
{
    [Fact]
    public void EveryCountryWithACatalogOtherThanColombiaHasASeedSource()
    {
        var withCatalog = CountryCatalogs.All
            .Select(catalog => catalog.Country)
            .Where(country => country != CountryCatalogs.Colombia)
            .Order();
        var seeded = RegionSeeder.EmbeddedSources.Select(source => source.Country).Order();

        Assert.Equal(withCatalog, seeded);
    }

    [Fact]
    public void EverySeedSourceOpensAndParses()
    {
        foreach (var source in RegionSeeder.EmbeddedSources)
        {
            using var regionsStream = source.OpenRegions();
            using var citiesStream = source.OpenCities();
            var regions = RegionDataParser.ParseRegions(regionsStream);
            var cities = RegionDataParser.ParseCities(citiesStream);

            Assert.NotEmpty(regions);
            RegionDataParser.Validate(regions, cities);
        }
    }
}

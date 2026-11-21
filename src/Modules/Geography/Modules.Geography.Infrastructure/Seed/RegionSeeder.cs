using Microsoft.EntityFrameworkCore;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;

namespace Modules.Geography.Infrastructure.Seed;

/// <summary>
/// Importador idempotente de las regiones y municipios de los países fuera de Colombia. Corre en
/// cada arranque (ver <see cref="GeographyDatabaseInitializer"/>), después de DIVIPOLA, y hace
/// upsert por <c>(country_code, code)</c> y por <c>(region, code)</c> contra los JSON embebidos.
/// Un país nuevo es una entrada más de <see cref="Countries"/> y sus dos archivos en Seed/Data; su
/// lista de <see cref="CountryCatalogs.All"/> se ajusta aparte.
///
/// Reconcilia, no sincroniza: un nombre que cambia entre años (el INE renombra municipios) se
/// actualiza, pero una fila que sale del archivo no se borra. Mismo criterio que DIVIPOLA.
/// </summary>
internal sealed class RegionSeeder(GeographyDbContext dbContext)
{
    // País → sufijos de sus recursos embebidos. Los JSON se copian de la fuente oficial del país (README § Geografía).
    private static readonly (string Country, string RegionsResource, string CitiesResource)[] Countries =
    [
        (CountryCatalogs.Spain, "Seed.Data.es-regions.json", "Seed.Data.es-cities.json"),
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var (country, regionsResource, citiesResource) in Countries)
        {
            var regionRecords = RegionDataParser.ParseRegions(GeographySeeder.OpenResource(regionsResource));
            var cityRecords = RegionDataParser.ParseCities(GeographySeeder.OpenResource(citiesResource));

            var regionsByCode = await SeedRegionsAsync(country, regionRecords, cancellationToken);
            await SeedCitiesAsync(regionsByCode, cityRecords, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<string, Region>> SeedRegionsAsync(
        string country, IReadOnlyList<RegionRecord> records, CancellationToken cancellationToken)
    {
        var regionsByCode = await dbContext.Regions
            .Where(region => region.CountryCode == country)
            .ToDictionaryAsync(region => region.Code, StringComparer.Ordinal, cancellationToken);

        foreach (var record in records)
        {
            if (regionsByCode.TryGetValue(record.Code, out var region))
            {
                region.Rename(record.Name);
                continue;
            }

            var created = Region.Create(RegionId.New(), country, record.Code, record.Name);
            dbContext.Regions.Add(created);
            regionsByCode[record.Code] = created;
        }

        return regionsByCode;
    }

    private async Task SeedCitiesAsync(
        Dictionary<string, Region> regionsByCode,
        IReadOnlyList<RegionCityRecord> records,
        CancellationToken cancellationToken)
    {
        var regionIds = regionsByCode.Values.Select(region => region.Id).ToArray();
        var existing = await dbContext.RegionCities
            .Where(city => regionIds.Contains(city.RegionId))
            .ToArrayAsync(cancellationToken);
        var citiesByKey = existing.ToDictionary(city => (city.RegionId, city.Code));

        foreach (var record in records)
        {
            if (!regionsByCode.TryGetValue(record.RegionCode, out var region))
            {
                throw new InvalidOperationException(
                    $"City '{record.Code}' references region code '{record.RegionCode}', " +
                    "which was not found among the seeded regions.");
            }

            if (citiesByKey.TryGetValue((region.Id, record.Code), out var city))
            {
                city.Rename(record.Name);
                continue;
            }

            dbContext.RegionCities.Add(
                RegionCity.Create(RegionCityId.New(), region.Id, record.Code, record.Name));
        }
    }
}

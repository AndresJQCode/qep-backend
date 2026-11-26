using Microsoft.EntityFrameworkCore;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;

namespace Modules.Geography.Infrastructure.Seed;

/// <summary>Los dos archivos de un país con catálogo fuera de Colombia, abiertos por demanda.</summary>
internal sealed record RegionSeedSource(string Country, Func<Stream> OpenRegions, Func<Stream> OpenCities);

/// <summary>
/// Importador idempotente de las regiones y municipios de los países fuera de Colombia. Corre en cada arranque (ver
/// <see cref="GeographyDatabaseInitializer"/>), después de DIVIPOLA, y está hecho para que un arranque normal no cueste
/// nada: la app en producción arranca muchas veces y los datos casi nunca cambian.
///
/// <list type="bullet">
/// <item>Por país calcula la huella de sus dos archivos (<see cref="RegionSeedHash"/>, lectura en bloques) y la compara
/// con la de <c>geography.seed_state</c>. Si coincide, termina: no parsea el JSON ni lee <c>regions</c> ni
/// <c>region_cities</c>.</item>
/// <item>Si no coincide (primer arranque, o un archivo nuevo del INE), parsea y valida **antes** de tocar la base y aplica
/// el país en una sola transacción con dos <c>INSERT … ON CONFLICT DO UPDATE</c> sobre <c>unnest</c> de arreglos. No
/// carga las filas existentes ni usa el change tracker: la base compara y sólo escribe lo que cambió. La huella se
/// guarda en la misma transacción, así que un fallo a medias no la deja marcada como aplicada.</item>
/// </list>
///
/// Reconcilia, no sincroniza: un nombre que cambia entre años se actualiza, pero una fila que sale del archivo no se
/// borra (mismo criterio que DIVIPOLA). Editar a mano <c>regions</c> o <c>region_cities</c> no se revierte hasta que el
/// archivo cambie: son datos de referencia que sólo escribe este importador.
///
/// Un país nuevo es una entrada más de <see cref="EmbeddedSources"/> con sus dos JSON en Seed/Data, y otra en
/// <see cref="CountryCatalogs.All"/>; <c>RegionSeederSourcesTests</c> falla si una de las dos listas se queda atrás.
/// </summary>
internal sealed class RegionSeeder(GeographyDbContext dbContext, IReadOnlyList<RegionSeedSource> sources)
{
    internal static IReadOnlyList<RegionSeedSource> EmbeddedSources { get; } =
    [
        new(
            CountryCatalogs.Spain,
            () => GeographySeeder.OpenResource("Seed.Data.es-regions.json"),
            () => GeographySeeder.OpenResource("Seed.Data.es-cities.json")),
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            await SeedCountryAsync(source, cancellationToken);
        }
    }

    private async Task SeedCountryAsync(RegionSeedSource source, CancellationToken cancellationToken)
    {
        var key = $"regions:{source.Country}";
        string hash;
        using (var regionsStream = source.OpenRegions())
        using (var citiesStream = source.OpenCities())
        {
            hash = RegionSeedHash.Compute(source.Country, regionsStream, citiesStream);
        }

        var stored = await dbContext.SeedStates
            .AsNoTracking()
            .Where(state => state.Key == key)
            .Select(state => state.ContentHash)
            .SingleOrDefaultAsync(cancellationToken);
        if (stored == hash)
        {
            return;
        }

        IReadOnlyList<RegionRecord> regionRecords;
        IReadOnlyList<RegionCityRecord> cityRecords;
        using (var regionsStream = source.OpenRegions())
        using (var citiesStream = source.OpenCities())
        {
            regionRecords = RegionDataParser.ParseRegions(regionsStream);
            cityRecords = RegionDataParser.ParseCities(citiesStream);
        }

        RegionDataParser.Validate(regionRecords, cityRecords);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var regionIdsByCode = await UpsertRegionsAsync(source.Country, regionRecords, cancellationToken);
        await UpsertCitiesAsync(regionIdsByCode, cityRecords, cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO geography.seed_state (key, content_hash) VALUES ({key}, {hash})
             ON CONFLICT (key) DO UPDATE SET content_hash = EXCLUDED.content_hash
             """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Las entidades de dominio sólo validan y recortan (no se rastrean ni se guardan): lo que viaja a la base son sus valores.
    private async Task<Dictionary<string, Guid>> UpsertRegionsAsync(
        string country, IReadOnlyList<RegionRecord> records, CancellationToken cancellationToken)
    {
        var regions = records
            .Select(record => Region.Create(RegionId.New(), country, record.Code, record.Name))
            .ToArray();
        var ids = regions.Select(region => region.Id.Value).ToArray();
        var countries = regions.Select(region => region.CountryCode).ToArray();
        var codes = regions.Select(region => region.Code).ToArray();
        var names = regions.Select(region => region.Name).ToArray();

        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO geography.regions (id, country_code, code, name)
             SELECT * FROM unnest({ids}, {countries}, {codes}, {names})
             ON CONFLICT (country_code, code) DO UPDATE SET name = EXCLUDED.name
             WHERE geography.regions.name IS DISTINCT FROM EXCLUDED.name
             """,
            cancellationToken);

        // Una región que ya existía conserva su id: el que vale es el de la base, no el recién generado.
        return await dbContext.Regions
            .AsNoTracking()
            .Where(region => region.CountryCode == country)
            .ToDictionaryAsync(region => region.Code, region => region.Id.Value, StringComparer.Ordinal, cancellationToken);
    }

    private async Task UpsertCitiesAsync(
        Dictionary<string, Guid> regionIdsByCode,
        IReadOnlyList<RegionCityRecord> records,
        CancellationToken cancellationToken)
    {
        var cities = records
            .Select(record => RegionCity.Create(
                RegionCityId.New(),
                new RegionId(regionIdsByCode[record.RegionCode]),
                record.Code,
                record.Name))
            .ToArray();
        var ids = cities.Select(city => city.Id.Value).ToArray();
        var regionIds = cities.Select(city => city.RegionId.Value).ToArray();
        var codes = cities.Select(city => city.Code).ToArray();
        var names = cities.Select(city => city.Name).ToArray();

        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO geography.region_cities (id, region_id, code, name)
             SELECT * FROM unnest({ids}, {regionIds}, {codes}, {names})
             ON CONFLICT (region_id, code) DO UPDATE SET name = EXCLUDED.name
             WHERE geography.region_cities.name IS DISTINCT FROM EXCLUDED.name
             """,
            cancellationToken);
    }
}

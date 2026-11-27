using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;

namespace Modules.Geography.Infrastructure.Seed;

/// <summary>
/// Importador idempotente de la lista de ciudades de Siigo fuera de Colombia (<c>geography.billing_cities</c>). Corre en
/// cada arranque, dentro de <see cref="GeographyDatabaseInitializer"/>, y con el mismo diseño que <see cref="RegionSeeder"/>:
/// calcula la huella del archivo y, si coincide con la de <c>geography.seed_state</c>, termina sin leer la tabla; si no,
/// parsea y valida antes de tocar la base y aplica todo en una transacción con un solo <c>INSERT … ON CONFLICT DO UPDATE</c>
/// sobre <c>unnest</c>.
///
/// Reconcilia, no sincroniza: un nombre que cambia se actualiza, pero una fila que sale del archivo **no se borra**, porque
/// un sinónimo del exterior puede apuntar a ella (FK restrict). Los datos del archivo se regeneran con
/// <c>scripts/geography/convert_siigo_cities.py</c> cuando Siigo publique una lista nueva.
/// </summary>
internal sealed class BillingCitySeeder(GeographyDbContext dbContext, Func<Stream> openSource)
{
    public const string ResourceSuffix = "Seed.Data.siigo-billing-cities.json";

    public const string StateKey = "billing-cities:siigo";

    /// <summary>
    /// Sube este número cuando cambie **la lógica** del importador y no sólo el archivo: sin esto, una corrección del
    /// importador no se aplicaría a una base cuya huella ya coincide.
    /// </summary>
    internal const int SeederVersion = 1;

    /// <summary>SHA-256 en hexadecimal minúscula de la versión del importador y los bytes crudos del archivo.</summary>
    internal static string ComputeHash(Stream source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"billing-cities-seed-v{SeederVersion}\n"));
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        string hash;
        using (var stream = openSource())
        {
            hash = ComputeHash(stream);
        }

        var stored = await dbContext.SeedStates
            .AsNoTracking()
            .Where(state => state.Key == StateKey)
            .Select(state => state.ContentHash)
            .SingleOrDefaultAsync(cancellationToken);
        if (stored == hash)
        {
            return;
        }

        IReadOnlyList<BillingCityRecord> records;
        using (var stream = openSource())
        {
            records = BillingCityDataParser.Parse(stream);
        }

        // Las entidades de dominio sólo validan y recortan (no se rastrean ni se guardan): lo que viaja a la base son sus valores.
        var cities = records
            .Select(record => BillingCity.Create(
                BillingCityId.New(),
                record.SiigoCountryCode,
                record.CountryName,
                record.StateCode,
                record.StateName,
                record.CityCode,
                record.CityName))
            .ToArray();
        var ids = cities.Select(city => city.Id.Value).ToArray();
        var countryCodes = cities.Select(city => city.CountryCode).ToArray();
        var siigoCountryCodes = cities.Select(city => city.SiigoCountryCode).ToArray();
        var countryNames = cities.Select(city => city.CountryName).ToArray();
        var stateCodes = cities.Select(city => city.StateCode).ToArray();
        var stateNames = cities.Select(city => city.StateName).ToArray();
        var cityCodes = cities.Select(city => city.CityCode).ToArray();
        var cityNames = cities.Select(city => city.CityName).ToArray();
        var normalizedNames = cities.Select(city => city.NormalizedName).ToArray();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO geography.billing_cities
                 (id, country_code, siigo_country_code, country_name, state_code, state_name, city_code, city_name, normalized_name)
             SELECT * FROM unnest(
                 {ids}, {countryCodes}, {siigoCountryCodes}, {countryNames}, {stateCodes}, {stateNames},
                 {cityCodes}, {cityNames}, {normalizedNames})
             ON CONFLICT (country_code, state_code, city_code) DO UPDATE SET
                 siigo_country_code = EXCLUDED.siigo_country_code,
                 country_name = EXCLUDED.country_name,
                 state_name = EXCLUDED.state_name,
                 city_name = EXCLUDED.city_name,
                 normalized_name = EXCLUDED.normalized_name
             WHERE geography.billing_cities.siigo_country_code IS DISTINCT FROM EXCLUDED.siigo_country_code
                OR geography.billing_cities.country_name IS DISTINCT FROM EXCLUDED.country_name
                OR geography.billing_cities.state_name IS DISTINCT FROM EXCLUDED.state_name
                OR geography.billing_cities.city_name IS DISTINCT FROM EXCLUDED.city_name
                OR geography.billing_cities.normalized_name IS DISTINCT FROM EXCLUDED.normalized_name
             """,
            cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO geography.seed_state (key, content_hash) VALUES ({StateKey}, {hash})
             ON CONFLICT (key) DO UPDATE SET content_hash = EXCLUDED.content_hash
             """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

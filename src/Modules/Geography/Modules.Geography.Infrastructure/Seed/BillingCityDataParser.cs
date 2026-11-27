using System.Text.Json;

namespace Modules.Geography.Infrastructure.Seed;

internal sealed record BillingCityRecord(
    string SiigoCountryCode,
    string CountryName,
    string StateCode,
    string StateName,
    string CityCode,
    string CityName);

// Forma del JSON que genera scripts/geography/convert_siigo_cities.py (siigo-billing-cities.json). Claves planas.
internal sealed record BillingCityJson(
    string? CountryCode,
    string? CountryName,
    string? StateCode,
    string? StateName,
    string? CityCode,
    string? CityName);

/// <summary>
/// Parsea y valida la lista de ciudades de Siigo antes de que <c>BillingCitySeeder</c> toque la base. Mismo criterio que
/// <see cref="RegionDataParser"/>: las fallas son <see cref="InvalidOperationException"/> porque ocurren al arrancar, y un
/// archivo mal formado tumba el arranque antes de sembrar nada.
/// </summary>
internal static class BillingCityDataParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<BillingCityRecord> Parse(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<List<BillingCityJson>>(stream, JsonOptions) ?? [];
        var seen = new HashSet<(string Country, string State, string City)>();
        var records = new List<BillingCityRecord>(raw.Count);
        foreach (var entry in raw)
        {
            var country = Require(entry.CountryCode, "country code");
            var record = new BillingCityRecord(
                country,
                Require(entry.CountryName, "country name"),
                Require(entry.StateCode, "state code"),
                Require(entry.StateName, "state name"),
                Require(entry.CityCode, "city code"),
                Require(entry.CityName, "city name"));

            // Colombia sigue por DANE (spec 2026-11-26): un Co en la lista es un archivo equivocado.
            if (country.Equals("CO", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Colombia must not be in the billing city list.");
            }

            if (!seen.Add((country.ToUpperInvariant(), record.StateCode, record.CityCode)))
            {
                throw new InvalidOperationException(
                    $"Duplicate billing city '{country}/{record.StateCode}/{record.CityCode}' in the source file.");
            }

            records.Add(record);
        }

        return records;
    }

    // Trim() recorta también el espacio de no separación (U+00A0).
    private static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"The {what} is required in the source file.")
            : value.Trim();
}

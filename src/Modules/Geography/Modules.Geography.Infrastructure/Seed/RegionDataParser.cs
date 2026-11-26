using System.Text.Json;

namespace Modules.Geography.Infrastructure.Seed;

internal sealed record RegionRecord(string Code, string Name);

internal sealed record RegionCityRecord(string Code, string Name, string RegionCode);

// Forma del JSON curado en Seed/Data (es-regions.json, es-cities.json). Claves en minúscula, planas.
internal sealed record RegionJson(string? Code, string? Name);

internal sealed record RegionCityJson(string? Code, string? Name, string? RegionCode);

/// <summary>
/// Parsea y valida los JSON de regiones y municipios de un país antes de que <c>RegionSeeder</c>
/// toque la base. Mismo criterio que <see cref="DivipolaDataParser"/>: las fallas son
/// <see cref="InvalidOperationException"/> porque ocurren al arrancar, fuera de un request, y un
/// archivo mal formado tumba el arranque antes de sembrar nada.
/// </summary>
internal static class RegionDataParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<RegionRecord> ParseRegions(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<List<RegionJson>>(stream, JsonOptions) ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<RegionRecord>(raw.Count);
        foreach (var entry in raw)
        {
            var code = Require(entry.Code, "region code");
            var name = Require(entry.Name, "region name");
            if (!seen.Add(code))
            {
                throw new InvalidOperationException($"Duplicate region code '{code}' in the source file.");
            }

            records.Add(new RegionRecord(code, name));
        }

        return records;
    }

    public static IReadOnlyList<RegionCityRecord> ParseCities(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<List<RegionCityJson>>(stream, JsonOptions) ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<RegionCityRecord>(raw.Count);
        foreach (var entry in raw)
        {
            var code = Require(entry.Code, "city code");
            var name = Require(entry.Name, "city name");
            var regionCode = Require(entry.RegionCode, "city regionCode");
            if (!code.StartsWith(regionCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"City code '{code}' does not start with its declared region code '{regionCode}'.");
            }

            if (!seen.Add(code))
            {
                throw new InvalidOperationException($"Duplicate city code '{code}' in the source file.");
            }

            records.Add(new RegionCityRecord(code, name, regionCode));
        }

        return records;
    }

    /// <summary>
    /// Lo que el seeder comprueba antes de tocar la base: toda ciudad cae en una región del mismo archivo. Un archivo
    /// regenerado a medias tumba el arranque con un mensaje claro y no deja datos a medias.
    /// </summary>
    public static void Validate(IReadOnlyList<RegionRecord> regions, IReadOnlyList<RegionCityRecord> cities)
    {
        var regionCodes = regions.Select(region => region.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var city in cities)
        {
            if (!regionCodes.Contains(city.RegionCode))
            {
                throw new InvalidOperationException(
                    $"City '{city.Code}' references region code '{city.RegionCode}', which is not in the regions file.");
            }
        }
    }

    private static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"The {what} is required in the source file.")
            : value.Trim();
}

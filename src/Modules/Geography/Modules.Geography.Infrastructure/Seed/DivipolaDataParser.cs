using System.Text.Json;

namespace Modules.Geography.Infrastructure.Seed;

internal sealed record DivipolaDepartmentRecord(string Code, string Name);

internal sealed record DivipolaCityRecord(string DivipolaCode, string Name, string DepartmentCode);

/// <summary>
/// Parsea y valida el JSON fuente de DIVIPOLA antes de que <see cref="GeographySeeder"/> toque la
/// base. El archivo de localidades trae dos niveles bajo el mismo array: municipios (código de 5
/// dígitos) y centros poblados/corregimientos (código de 8 dígitos, anidados bajo un municipio vía
/// el campo "municipality", que este parser ignora porque no lo necesita). Los dos niveles cuentan
/// como "ciudad" para este módulo y se importan ambos.
///
/// Las excepciones son <see cref="InvalidOperationException"/> y no
/// <c>GeographyDomainException</c> a propósito: esto corre al arrancar la app, fuera de un
/// request HTTP, así que no hay nada que mapear a un código de estado — es una falla de arranque,
/// no una respuesta de API.
/// </summary>
internal static class DivipolaDataParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<DivipolaDepartmentRecord> ParseDepartments(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<List<DivipolaJsonDepartment>>(stream, JsonOptions)
            ?? [];

        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<DivipolaDepartmentRecord>(raw.Count);
        foreach (var entry in raw)
        {
            var code = RequireCode(entry.Code, 2, "department");
            var name = RequireName(entry.Name, "department");
            if (!seenCodes.Add(code))
            {
                throw new InvalidOperationException(
                    $"Duplicate department DIVIPOLA code '{code}' in the source file.");
            }

            records.Add(new DivipolaDepartmentRecord(code, name));
        }

        return records;
    }

    public static IReadOnlyList<DivipolaCityRecord> ParseCities(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<List<DivipolaJsonCity>>(stream, JsonOptions)
            ?? [];

        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<DivipolaCityRecord>(raw.Count);
        foreach (var entry in raw)
        {
            var code = RequireCityCode(entry.Code);
            var name = RequireName(entry.Name, "city");
            var departmentCode = RequireCode(entry.Department?.Code, 2, "city.department");

            if (!code.StartsWith(departmentCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"City DIVIPOLA code '{code}' does not start with its declared " +
                    $"department code '{departmentCode}'.");
            }

            if (!seenCodes.Add(code))
            {
                throw new InvalidOperationException(
                    $"Duplicate city DIVIPOLA code '{code}' in the source file.");
            }

            records.Add(new DivipolaCityRecord(code, name, departmentCode));
        }

        return records;
    }

    private static string RequireCode(string? code, int length, string entity)
    {
        if (string.IsNullOrEmpty(code) || code.Length != length || !code.All(char.IsAsciiDigit))
        {
            throw new InvalidOperationException(
                $"The {entity} DIVIPOLA code must be exactly {length} digits, got " +
                $"'{code ?? "<null>"}'.");
        }

        return code;
    }

    // La ciudad cubre dos niveles del archivo fuente bajo el mismo código: municipio (5 dígitos)
    // y centro poblado/corregimiento (8 dígitos, con el municipio como sus primeros 5 dígitos).
    // Ninguna otra longitud aparece en el archivo real.
    private static string RequireCityCode(string? code)
    {
        if (string.IsNullOrEmpty(code) ||
            code.Length is not (5 or 8) ||
            !code.All(char.IsAsciiDigit))
        {
            throw new InvalidOperationException(
                "The city DIVIPOLA code must be exactly 5 or 8 digits, got " +
                $"'{code ?? "<null>"}'.");
        }

        return code;
    }

    private static string RequireName(string? name, string entity)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"The {entity} name is required.");
        }

        return name;
    }
}

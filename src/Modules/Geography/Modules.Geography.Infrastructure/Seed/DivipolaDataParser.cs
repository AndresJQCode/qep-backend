using System.Text.Json;

namespace Modules.Geography.Infrastructure.Seed;

internal sealed record DivipolaDepartmentRecord(string Code, string Name);

internal sealed record DivipolaCityRecord(string DivipolaCode, string Name, string DepartmentCode);

/// <summary>
/// Parsea y valida el JSON fuente de DIVIPOLA antes de que <see cref="GeographySeeder"/> toque la
/// base. El archivo de localidades trae dos niveles bajo el mismo array, planos: municipios
/// (código de 5 dígitos) y centros poblados/corregimientos (código de 8 dígitos). Sólo el nivel
/// municipio se importa como "ciudad" — los centros poblados se descartan acá porque su nombre se
/// repite masivamente en todo el país (nombres de vereda/corregimiento como "SAN ANTONIO" o
/// "BUENAVISTA" aparecen decenas de veces), lo que dejaba un selector de ciudad con nombres
/// duplicados dentro de un mismo departamento. El municipio, en cambio, es único por departamento.
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
            // Sólo el nivel municipio: los centros poblados (código de 8 dígitos) se descartan
            // acá, no son un error de datos — el archivo fuente los trae a propósito, este módulo
            // no los quiere.
            if (entry.Code is not { Length: 5 })
            {
                continue;
            }

            var code = RequireCityCode(entry.Code);
            var name = RequireName(entry.Name, "city");
            var departmentCode = RequireCode(entry.DepartmentCode, 2, "city.departmentCode");

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

    private static string RequireCityCode(string? code)
    {
        if (string.IsNullOrEmpty(code) ||
            code.Length != 5 ||
            !code.All(char.IsAsciiDigit))
        {
            throw new InvalidOperationException(
                "The city DIVIPOLA code must be exactly 5 digits, got " +
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

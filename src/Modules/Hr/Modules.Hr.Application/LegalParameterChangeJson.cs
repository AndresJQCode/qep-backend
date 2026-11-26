using System.Globalization;
using System.Text.Json;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// El JSON de las columnas <c>old_value</c> y <c>new_value</c> del historial: un número (<c>1750905</c>) o la lista de tramos en
/// camelCase. Los números salen sin ceros de más, para que «1750905.000000» y «1750905» no se vean como un cambio. Postgres guarda
/// <c>jsonb</c> y reordena las propiedades: la lectura no depende del orden.
/// </summary>
public static class LegalParameterChangeJson
{
    public static string Number(decimal value) => Normalize(value).ToString(CultureInfo.InvariantCulture);

    public static string Brackets(IReadOnlyList<LegalBracketInput> brackets)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var bracket in brackets)
            {
                writer.WriteStartObject();
                writer.WriteNumber("lowerBound", Normalize(bracket.LowerBound));
                if (bracket.UpperBound is { } upper)
                {
                    writer.WriteNumber("upperBound", Normalize(upper));
                }
                else
                {
                    writer.WriteNull("upperBound");
                }

                writer.WriteNumber("rate", Normalize(bracket.Rate));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static decimal? ReadNumber(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Number ? document.RootElement.GetDecimal() : null;
    }

    public static IReadOnlyList<LegalBracketInput>? ReadBrackets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var brackets = new List<LegalBracketInput>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            decimal? upper = element.TryGetProperty("upperBound", out var upperElement) && upperElement.ValueKind == JsonValueKind.Number
                ? upperElement.GetDecimal()
                : null;
            brackets.Add(new LegalBracketInput(
                element.GetProperty("lowerBound").GetDecimal(), upper, element.GetProperty("rate").GetDecimal()));
        }

        return brackets;
    }

    // Dividir por 1 con la escala máxima quita los ceros finales de un decimal (4.0000 pasa a 4).
    private static decimal Normalize(decimal value) => value / 1.000000000000000000000000000000000m;
}

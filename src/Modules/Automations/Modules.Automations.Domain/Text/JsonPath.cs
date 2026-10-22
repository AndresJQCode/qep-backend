using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Modules.Automations.Domain.Text;

/// <summary>
/// Rutas por puntos e índices sobre un JSON (<c>cliente.firstName</c>, <c>data[0].id</c>): las que usan las variables, el mapeo de la
/// respuesta de una API externa y las reglas de Condición (spec 2026-10-21 D-H4, D-M8). Es de sólo lectura y nunca lanza: una ruta
/// mal escrita o ausente es <c>null</c>.
/// </summary>
public static partial class JsonPath
{
    // Un nombre, y luego cualquier mezcla de «.nombre» e «[n]».
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*(?:\.[a-zA-Z_][a-zA-Z0-9_]*|\[[0-9]+\])*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    [GeneratedRegex(@"[a-zA-Z_][a-zA-Z0-9_]*|\[([0-9]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    public static bool IsValid(string? path) => !string.IsNullOrEmpty(path) && Shape().IsMatch(path);

    public static JsonNode? Select(JsonNode? root, string? path)
    {
        if (root is null || !IsValid(path))
        {
            return null;
        }

        var current = root;
        foreach (Match segment in Segment().Matches(path!))
        {
            if (segment.Groups[1].Success)
            {
                // Un índice que no cabe en int (o fuera de rango) es ausente, no un error.
                if (current is not JsonArray array || !int.TryParse(segment.Groups[1].Value, out var index) || index >= array.Count)
                {
                    return null;
                }

                current = array[index];
            }
            else
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment.Value, out var next))
                {
                    return null;
                }

                current = next;
            }

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}

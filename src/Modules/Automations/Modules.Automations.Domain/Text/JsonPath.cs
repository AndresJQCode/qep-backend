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

    // Ruling P2-R36: la ruta del mapeo de una respuesta, además, puede ser «$» (el cuerpo entero), empezar por «$» seguido de «.nombre» o
    // «[n]», o empezar por un índice: las API del Anexo C responden arreglos en la raíz (`[].id`).
    [GeneratedRegex(@"^(?:\$(?:\.[a-zA-Z_][a-zA-Z0-9_]*|\[[0-9]+\])*|(?:[a-zA-Z_][a-zA-Z0-9_]*|\[[0-9]+\])(?:\.[a-zA-Z_][a-zA-Z0-9_]*|\[[0-9]+\])*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResponseShape();

    /// <summary>D-H4 y ruling P2-R36: la forma que acepta <c>mapping.from</c> de un bloque HTTP (el validador y el ejecutor usan ésta). Las
    /// referencias a variables siguen con <see cref="IsValid"/>: <c>$</c> no nombra una variable.</summary>
    public static bool IsValidResponsePath(string? path) => !string.IsNullOrEmpty(path) && ResponseShape().IsMatch(path);

    /// <summary>Lee <paramref name="path"/> sobre la respuesta de una API; <c>$</c> es <paramref name="root"/> entero. Nunca lanza.</summary>
    public static JsonNode? SelectResponse(JsonNode? root, string? path)
    {
        if (root is null || !IsValidResponsePath(path))
        {
            return null;
        }

        return Walk(root, path![0] == '$' ? path[1..] : path);
    }

    public static bool IsValid(string? path) => !string.IsNullOrEmpty(path) && Shape().IsMatch(path);

    public static JsonNode? Select(JsonNode? root, string? path) => root is null || !IsValid(path) ? null : Walk(root, path!);

    /// <summary>Recorre los segmentos de una ruta ya validada; una vacía es la raíz.</summary>
    private static JsonNode? Walk(JsonNode root, string path)
    {
        var current = root;
        foreach (Match segment in Segment().Matches(path))
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

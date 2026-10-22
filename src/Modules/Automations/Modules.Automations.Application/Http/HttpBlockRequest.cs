using System.Text.Json;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Application.Http;

/// <summary>
/// Spec 2026-10-21 D-H4: lo que el bloque HTTP va a mandar, ya interpolado con las variables de la corrida y sin tocar la red. La ruta
/// lleva cada valor escapado con <c>Uri.EscapeDataString</c> (fallo P2-R31: lo que dijo el cliente no cambia la ruta ni le agrega query) y
/// sin query, que es lo que guarda <c>http_calls.path</c>; la query viaja aparte, con sus valores sin escapar (se escapan al armar la URL).
/// </summary>
public sealed record HttpBlockRequest(string Method, string Path, IReadOnlyList<KeyValuePair<string, string>> Query, HttpBlockBody? Body)
{
    /// <summary>
    /// Fallo P2-R31: un segmento que quedó en <c>.</c> o <c>..</c> (también codificado). <c>Uri.EscapeDataString</c> no escapa el punto, y
    /// <c>System.Uri</c> resuelve <c>..</c> y <c>%2E%2E</c> hacia arriba: un valor <c>..</c> sacaría la llamada de la URL base de la conexión.
    /// El validador (P2-R8) ya lo rechaza en el texto de la plantilla; esto mira la ruta ya interpolada.
    /// </summary>
    public bool PathHasDotSegment => Path.Split('/').Any(segment => Uri.UnescapeDataString(segment) is "." or "..");

    public static HttpBlockRequest Build(HttpNode node, RunVariables variables)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(variables);
        return new HttpBlockRequest(
            node.Method,
            TemplateText.Interpolate(node.Path, variables, Uri.EscapeDataString),
            Pairs(node.Query, variables),
            BodyOf(node.Body, variables));
    }

    private static HttpBlockBody? BodyOf(HttpBody? body, RunVariables variables) => body switch
    {
        null => null,
        { Kind: HttpBodyKinds.Multipart } => new HttpBlockBody.Multipart(Pairs(body.Fields ?? [], variables), body.FileField, body.FileVariable),
        // Un json sin plantilla no se publica (FlowValidator); si llega, el vacío no es JSON y sale por body_not_json sin llamar.
        _ => new HttpBlockBody.Json(TemplateText.InterpolateJson(body.Template ?? string.Empty, variables)),
    };

    private static KeyValuePair<string, string>[] Pairs(IReadOnlyList<KeyValue> pairs, RunVariables variables) =>
        [.. pairs.Select(pair => new KeyValuePair<string, string>(pair.Key, TemplateText.Interpolate(pair.Value, variables)))];
}

/// <summary>El cuerpo del bloque HTTP (D-H4): una plantilla JSON ya interpolada o un formulario multipart con campos de texto y, a lo sumo, un
/// archivo ligado a una variable de adjunto (<see cref="Multipart.FileVariable"/>: la que guardó el bloque Adjunto, con su <c>messageId</c>).</summary>
public abstract record HttpBlockBody
{
    public sealed record Json(string Content) : HttpBlockBody
    {
        /// <summary>Fallo P2-R11: el cuerpo se vuelve a leer como JSON antes de mandarlo; si no lo es, el bloque no llama.</summary>
        public bool IsWellFormed()
        {
            try
            {
                using var _ = JsonDocument.Parse(Content);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>Sin <see cref="FileField"/> o sin <see cref="FileVariable"/> el formulario va sólo con los campos.</summary>
    public sealed record Multipart(IReadOnlyList<KeyValuePair<string, string>> Fields, string? FileField, string? FileVariable) : HttpBlockBody;
}

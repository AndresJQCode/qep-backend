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
    /// Por qué la ruta no puede salir, o <c>null</c>. <c>path_value_empty</c> (P2-R34): un valor vacío o ausente (<c>/customers/{id}</c> sin id
    /// caería sobre la colección con un PUT o PATCH). <c>path_dot_segment</c> (P2-R31, P2-R32, P2-R34): un valor con un segmento <c>.</c> o
    /// <c>..</c>, mirado ya desescapado y partido por <c>/</c> y <c>\</c>, o la ruta interpolada con uno. Escapado, <c>../admin</c> queda en
    /// un solo segmento (<c>..%2Fadmin</c>), pero un proxy que decodifica <c>%2F</c> y normaliza (nginx con URI en <c>proxy_pass</c>) subiría
    /// igual; y <c>System.Uri</c> resuelve <c>..</c> y <c>%2E%2E</c> hacia arriba de la URL base de la conexión. El validador (P2-R8) ya rechaza
    /// <c>..</c> en el texto de la plantilla.
    /// </summary>
    public string? PathRejection { get; private init; }

    public static HttpBlockRequest Build(HttpNode node, RunVariables variables)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(variables);
        var values = new List<string>();
        var path = TemplateText.Interpolate(node.Path, variables, value =>
        {
            values.Add(value);
            return Uri.EscapeDataString(value);
        });
        return new HttpBlockRequest(node.Method, path, Pairs(node.Query, variables), BodyOf(node.Body, variables))
        {
            PathRejection = PathRejectionOf(path, values),
        };
    }

    private static string? PathRejectionOf(string path, IReadOnlyList<string> values)
    {
        if (values.Any(value => value.Length == 0))
        {
            return "path_value_empty";
        }

        return values.Any(value => HasDotSegment(FullyUnescaped(value), '/', '\\')) || HasDotSegment(FullyUnescaped(path), '/')
            ? "path_dot_segment"
            : null;
    }

    private static bool HasDotSegment(string text, params char[] separators) =>
        text.Split(separators).Any(segment => segment is "." or "..");

    // Un servidor que decodifica dos veces vería «..» en «%252E%252E»: se desescapa hasta que no cambie (con tope, por si acaso).
    private static string FullyUnescaped(string text)
    {
        for (var round = 0; round < 4; round++)
        {
            var unescaped = Uri.UnescapeDataString(text);
            if (unescaped == text)
            {
                return text;
            }

            text = unescaped;
        }

        return text;
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

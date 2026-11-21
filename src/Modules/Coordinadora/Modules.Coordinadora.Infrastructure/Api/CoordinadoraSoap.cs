using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Modules.Coordinadora.Infrastructure.Api;

/// <summary>
/// El sobre SOAP 1.2 del servidor de guías (spec 2026-11-21 §3, V-1). El cuerpo <c>p</c> se arma desde el JSON del armador: cada propiedad
/// es un elemento, un objeto anidado son elementos anidados y un arreglo es un elemento con un <c>item</c> por entrada (que es como el
/// cliente PHP de referencia manda <c>detalle</c>). Las credenciales se agregan al final de <c>p</c>, nunca al JSON que se guarda. La
/// respuesta se lee sin esquema: el <c>Fault</c>, el <c>codigo_remision</c> y cualquier texto que sea un PDF en base64 (V-2).
/// </summary>
internal static class CoordinadoraSoap
{
    public static readonly XNamespace Envelope = "http://www.w3.org/2003/05/soap-envelope";

    public const string ContentType = "application/soap+xml";

    /// <summary>Los bytes de un PDF (<c>%PDF-</c>) en base64.</summary>
    private const string PdfBase64Prefix = "JVBERi0";

    public static string Build(string ns, string operation, string parametersJson, IReadOnlyDictionary<string, string> credentials)
    {
        XNamespace target = ns;
        var p = new XElement("p");
        using (var document = JsonDocument.Parse(parametersJson))
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                p.Add(ToElement(property.Name, property.Value));
            }
        }

        foreach (var (name, value) in credentials)
        {
            p.Add(new XElement(name, value));
        }

        var envelope = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                Envelope + "Envelope",
                new XAttribute(XNamespace.Xmlns + "env", Envelope.NamespaceName),
                new XElement(Envelope + "Body", new XElement(target + operation, new XAttribute(XNamespace.Xmlns + "ns", ns), p))));
        return envelope.Declaration + envelope.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement ToElement(string name, JsonElement value)
    {
        var element = new XElement(name);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    element.Add(ToElement(property.Name, property.Value));
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    element.Add(ToElement("item", item));
                }

                break;
            case JsonValueKind.String:
                element.Value = value.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number:
                element.Value = value.GetRawText();
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                element.Value = value.GetBoolean() ? "1" : "0";
                break;
            default:
                break;
        }

        return element;
    }

    /// <summary>Lo leído de una respuesta: el <c>Fault</c> si lo hay, y los elementos del cuerpo por nombre local.</summary>
    public sealed record Reply(XDocument Document, (string Code, string Message)? Fault)
    {
        /// <summary>El texto del primer elemento con ese nombre local (sin importar el espacio de nombres), o <c>null</c>.</summary>
        public string? Text(params string[] localNames)
        {
            foreach (var name in localNames)
            {
                var element = Document.Descendants().FirstOrDefault(candidate =>
                    string.Equals(candidate.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)
                    && !candidate.HasElements
                    && !string.IsNullOrWhiteSpace(candidate.Value));
                if (element is not null)
                {
                    return element.Value.Trim();
                }
            }

            return null;
        }

        /// <summary>El primer texto que sea un PDF en base64, ya decodificado; <c>null</c> si no hay (V-2).</summary>
        public byte[]? Pdf()
        {
            foreach (var element in Document.Descendants().Where(candidate => !candidate.HasElements))
            {
                var text = element.Value.Trim();
                if (text.StartsWith(PdfBase64Prefix, StringComparison.Ordinal))
                {
                    try
                    {
                        return Convert.FromBase64String(text);
                    }
                    catch (FormatException)
                    {
                        // Un texto que empieza como un PDF pero no es base64 válido no es el rótulo: se sigue buscando.
                    }
                }
            }

            return null;
        }

        /// <summary>El texto del cuerpo, aplanado, para un rastreo cuyos campos no se conocen (V-5). Un elemento cuyo nombre contenga
        /// alguno de <paramref name="excludedNameFragments"/> sale sin su valor: es lo que evita que el destinatario llegue a <c>tracking_detail</c>.</summary>
        public string BodyText(IReadOnlyCollection<string>? excludedNameFragments = null)
        {
            var body = Document.Descendants(Envelope + "Body").FirstOrDefault() ?? Document.Root;
            if (body is null)
            {
                return string.Empty;
            }

            return string.Join(
                " | ",
                body.Descendants()
                    .Where(element => !element.HasElements && !string.IsNullOrWhiteSpace(element.Value))
                    .Select(element => IsExcluded(element.Name.LocalName, excludedNameFragments)
                        ? $"{element.Name.LocalName}: …"
                        : $"{element.Name.LocalName}: {element.Value.Trim()}"));
        }

        private static bool IsExcluded(string localName, IReadOnlyCollection<string>? fragments) =>
            fragments is not null
            && fragments.Any(fragment => localName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>null</c> si el cuerpo no es XML.</summary>
    public static Reply? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(body, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var fault = document.Descendants(Envelope + "Fault").FirstOrDefault()
            ?? document.Descendants().FirstOrDefault(element => string.Equals(element.Name.LocalName, "Fault", StringComparison.Ordinal));
        if (fault is null)
        {
            return new Reply(document, null);
        }

        // SOAP 1.2: Code/Value y Reason/Text; SOAP 1.1: faultcode y faultstring. Se leen los dos por si el servidor contesta en 1.1.
        var code = LocalText(fault, "Value") ?? LocalText(fault, "faultcode") ?? "fault";
        var message = LocalText(fault, "Text") ?? LocalText(fault, "faultstring") ?? string.Empty;
        return new Reply(document, (code, message));
    }

    private static string? LocalText(XElement scope, string localName) =>
        scope.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal) && !element.HasElements)
            ?.Value.Trim();

    /// <summary>Si el código del Fault es del lado del cliente: <c>Sender</c> (SOAP 1.2) o <c>Client</c> (SOAP 1.1), con o sin prefijo. Todo
    /// lo demás (<c>Receiver</c>, <c>Server</c>, un código propio) es del servidor: no dice que la operación no haya quedado hecha.</summary>
    public static bool IsClientFault(string code) =>
        FaultKind(code) is "sender" or "client";

    /// <summary>La parte local del código del Fault, en minúsculas (<c>env:Sender</c> → <c>sender</c>).</summary>
    public static string FaultKind(string code)
    {
        var local = (code ?? string.Empty).Trim();
        var colon = local.LastIndexOf(':');
        return (colon >= 0 ? local[(colon + 1)..] : local).ToLowerInvariant();
    }

    /// <summary>Un número que Coordinadora devuelve como texto (<c>"12345.00"</c>), con cultura invariante.</summary>
    public static decimal? Number(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
}

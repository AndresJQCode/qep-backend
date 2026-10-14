using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-11 §5.2: <c>type</c> es <c>QuickReply</c>, <c>Url</c>, <c>PhoneNumber</c> u <c>Other</c>; <c>url</c> sólo
/// viaja en los de tipo <c>Url</c>.</summary>
public sealed record TemplateButtonDto(string Type, string Text, string? Url);

/// <summary>
/// Spec 2026-10-11 §5.1. BFF: <c>parameters</c> son las claves en orden de aparición (<c>"1"</c>, <c>"first_name"</c>) y
/// <c>sendable</c> con <c>unsupportedReason</c> viajan resueltos, para que la pantalla pinte las plantillas que v1 no sabe
/// enviar en gris y diga por qué, sin conocer el formato de componentes de Meta. <c>parameterFormat</c> es <c>Named</c> o
/// <c>Positional</c>.
/// </summary>
public sealed record MessageTemplateDto(
    string Name,
    string Language,
    string Category,
    string ParameterFormat,
    string? Header,
    string Body,
    string? Footer,
    IReadOnlyList<TemplateButtonDto> Buttons,
    IReadOnlyList<string> Parameters,
    bool Sendable,
    string? UnsupportedReason);

/// <summary>Spec 2026-10-11 §5.2: el <c>details</c> de un mensaje <c>Template</c>:
/// <c>{ "template": { "name", "language", "category", "parameters": { clave: valor } } }</c>.</summary>
public static class TemplateDetailsJson
{
    public static string Serialize(string name, string language, string category, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = new JsonObject();
        foreach (var (key, value) in parameters)
        {
            values[key] = value;
        }

        return new JsonObject
        {
            ["template"] = new JsonObject { ["name"] = name, ["language"] = language, ["category"] = category, ["parameters"] = values },
        }.ToJsonString();
    }

    /// <summary>Si el detalle falta o no trae nombre e idioma, <c>null</c>: el mensaje sale sin <c>template</c> en vez de romper la página.</summary>
    public static MessageTemplateRefDto? ParseRef(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.Object
                && Text(template, "name") is { Length: > 0 } name && Text(template, "language") is { Length: > 0 } language
                ? new MessageTemplateRefDto(name, language)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>La conexión de WhatsApp a la que se le pueden pedir las plantillas: <c>wabaId</c> y token en claro para ese request.
/// <see cref="ToString"/> no imprime el token.</summary>
public sealed record MessagingTemplateSource(string WabaId, string AccessToken)
{
    public override string ToString() => $"MessagingTemplateSource {{ WabaId = {WabaId} }}";
}

/// <summary>Un valor del cuerpo de la plantilla, con la clave de la plantilla (<c>"1"</c>, <c>"first_name"</c>).</summary>
public sealed record TemplateParameter(string Key, string Value);

/// <summary>
/// Spec 2026-10-11 §5.1: lo que viaja a Meta para enviar una plantilla. <c>Parameters</c> van en el orden de la plantilla;
/// con <c>Named</c> cada uno lleva su <c>parameter_name</c> y con posicionales sólo el orden. Sin parámetros no viaja
/// <c>components</c>. Lo reutiliza el worker de campañas.
/// </summary>
public sealed record TemplateSend(string Name, string Language, bool Named, IReadOnlyList<TemplateParameter> Parameters);

/// <summary>R10: lo que sale de armar una plantilla con sus valores, igual para el envío humano y para el worker.</summary>
public sealed record BuiltTemplate(TemplateSend Send, string Text, string DetailsJson);

/// <summary>Spec 2026-10-11 §5.2: <c>GET /connections</c>. <c>displayPhoneNumber</c> es <c>null</c> si la conexión no lo guardó.</summary>
public sealed record MessagingConnectionDto(Guid Id, string Name, string? DisplayPhoneNumber);

public static class TemplateUnsupportedReasons
{
    public const string MediaHeader = "media_header";
    public const string DynamicUrlButton = "dynamic_url_button";
    public const string Authentication = "authentication";
    public const string Carousel = "carousel";
    public const string Flow = "flow";
    public const string Other = "other";
}

/// <summary>
/// Spec 2026-10-11 §5.1: el normalizador puro de lo que devuelve <c>GET /{waba}/message_templates</c>. Sólo se queda con las
/// <c>APPROVED</c>; el resto de los estados (pausada, rechazada, en revisión) no se puede enviar y no tiene sentido mostrarlo.
/// Una plantilla que v1 no sabe enviar se conserva con <c>sendable = false</c> y su motivo (decisión 7).
/// </summary>
public static partial class TemplateCatalog
{
    public const string Named = "Named";
    public const string Positional = "Positional";

    public static IReadOnlyList<MessageTemplateDto> Normalize(IEnumerable<JsonElement> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var result = new List<MessageTemplateDto>();
        foreach (var raw in templates)
        {
            if (raw.ValueKind == JsonValueKind.Object
                && Text(raw, "status") is { } status && string.Equals(status, "APPROVED", StringComparison.OrdinalIgnoreCase)
                && Text(raw, "name") is { Length: > 0 } name && Text(raw, "language") is { Length: > 0 } language)
            {
                result.Add(Parse(raw, name, language));
            }
        }

        return result
            .OrderBy(template => template.Name, StringComparer.Ordinal)
            .ThenBy(template => template.Language, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Spec 2026-10-14 D-T8: una fila del espejo, normalizada igual que un elemento del listado de Meta. Se arma el objeto que Meta
    /// devolvería (nombre, idioma, categoría, formato y <c>components</c>) y se pasa por el mismo <see cref="Parse"/>: una sola copia de
    /// las reglas. Unos <c>components</c> ilegibles cuentan como vacíos.
    /// </summary>
    public static MessageTemplateDto FromStored(string name, string language, string category, string parameterFormat, string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var raw = new JsonObject
        {
            ["name"] = name,
            ["language"] = language,
            ["category"] = category,
            ["parameter_format"] = parameterFormat,
        };
        JsonNode? components = null;
        try
        {
            components = componentsJson.Length == 0 ? null : JsonNode.Parse(componentsJson);
        }
        catch (JsonException)
        {
            // Ilegible = vacío.
        }

        raw["components"] = components is JsonArray array ? array : new JsonArray();
        using var document = JsonDocument.Parse(raw.ToJsonString());
        return Parse(document.RootElement, name, language);
    }

    /// <summary>
    /// Spec 2026-10-11 §5.2: la plantilla del catálogo por nombre e idioma. Si no está o v1 no sabe enviarla,
    /// <c>messaging.template.not_available</c>; si las claves no son <b>exactamente</b> las de la plantilla (ni una más, ni una
    /// menos, ni otra capitalización), <c>messaging.template.parameters_mismatch</c>. Ambas son 422. Las reutiliza la validación
    /// de una campaña.
    /// </summary>
    public static MessageTemplateDto RequireSendable(
        IReadOnlyList<MessageTemplateDto> catalog, string name, string language, IEnumerable<string> parameterKeys)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(parameterKeys);
        var template = catalog.FirstOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal) && string.Equals(candidate.Language, language, StringComparison.Ordinal));
        if (template is not { Sendable: true })
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateNotAvailable, "The template is not an approved, sendable template of this connection.");
        }

        if (!template.Parameters.ToHashSet(StringComparer.Ordinal).SetEquals(parameterKeys))
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateParametersMismatch, "The parameters do not match the template's variables.");
        }

        return template;
    }

    /// <summary>
    /// R10: de la plantilla y sus valores, lo que viaja a Meta (<see cref="TemplateSend"/>, parámetros en el orden de la plantilla), el texto
    /// que verá la persona y el <c>details</c> del mensaje. El envío humano y el worker de campañas arman lo mismo por acá: dos copias se
    /// separarían la primera vez que alguien tocara una.
    /// </summary>
    public static BuiltTemplate BuildSend(MessageTemplateDto template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        var send = new TemplateSend(
            template.Name,
            template.Language,
            template.ParameterFormat == Named,
            [.. template.Parameters.Select(key => new TemplateParameter(key, values[key]))]);
        return new BuiltTemplate(
            send, Render(template, values), TemplateDetailsJson.Serialize(template.Name, template.Language, template.Category, values));
    }

    /// <summary>Lo que verá la persona: encabezado, cuerpo con las variables sustituidas y pie, separados por una línea en
    /// blanco. Una variable sin valor se deja tal cual.</summary>
    public static string Render(MessageTemplateDto template, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(parameters);
        var body = Placeholder().Replace(
            template.Body, match => parameters.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
        return string.Join("\n\n", new[] { template.Header, body, template.Footer }.Where(part => !string.IsNullOrEmpty(part)));
    }

    // Si hay dos motivos, gana el más específico de la lista: autenticación, carrusel, flujo, encabezado multimedia, URL
    // dinámica y, al final, «otro». La pantalla muestra uno solo.
    private static readonly string[] ReasonPriority =
    [
        TemplateUnsupportedReasons.Authentication, TemplateUnsupportedReasons.Carousel, TemplateUnsupportedReasons.Flow,
        TemplateUnsupportedReasons.MediaHeader, TemplateUnsupportedReasons.DynamicUrlButton, TemplateUnsupportedReasons.Other,
    ];

    private static MessageTemplateDto Parse(JsonElement raw, string name, string language)
    {
        var category = Text(raw, "category") ?? string.Empty;
        string? header = null;
        string? footer = null;
        var body = string.Empty;
        var buttons = new List<TemplateButtonDto>();
        string? reason = string.Equals(category, "AUTHENTICATION", StringComparison.OrdinalIgnoreCase) ? TemplateUnsupportedReasons.Authentication : null;

        if (raw.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in components.EnumerateArray().Where(component => component.ValueKind == JsonValueKind.Object))
            {
                switch (Text(component, "type")?.ToUpperInvariant())
                {
                    case "HEADER":
                        if (string.Equals(Text(component, "format"), "TEXT", StringComparison.OrdinalIgnoreCase))
                        {
                            header = Text(component, "text");
                            // Decisión 7: el encabezado de texto es sin variables.
                            reason = Worst(reason, header is not null && header.Contains("{{", StringComparison.Ordinal) ? TemplateUnsupportedReasons.Other : null);
                        }
                        else
                        {
                            reason = Worst(reason, TemplateUnsupportedReasons.MediaHeader);
                        }

                        break;
                    case "BODY":
                        body = Text(component, "text") ?? string.Empty;
                        break;
                    case "FOOTER":
                        footer = Text(component, "text");
                        break;
                    case "BUTTONS":
                        foreach (var (dto, buttonReason) in ButtonsOf(component))
                        {
                            buttons.Add(dto);
                            reason = Worst(reason, buttonReason);
                        }

                        break;
                    case "CAROUSEL":
                        reason = Worst(reason, TemplateUnsupportedReasons.Carousel);
                        break;
                    default:
                        reason = Worst(reason, TemplateUnsupportedReasons.Other);
                        break;
                }
            }
        }

        var parameters = Placeholder().Matches(body).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        var format = string.Equals(Text(raw, "parameter_format"), "NAMED", StringComparison.OrdinalIgnoreCase) ? Named : Positional;
        return new MessageTemplateDto(name, language, category, format, header, body, footer, buttons, parameters, reason is null, reason);
    }

    private static IEnumerable<(TemplateButtonDto Dto, string? Reason)> ButtonsOf(JsonElement component)
    {
        if (!component.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var button in buttons.EnumerateArray().Where(button => button.ValueKind == JsonValueKind.Object))
        {
            var text = Text(button, "text") ?? string.Empty;
            switch (Text(button, "type")?.ToUpperInvariant())
            {
                case "QUICK_REPLY":
                    yield return (new TemplateButtonDto("QuickReply", text, null), null);
                    break;
                case "URL":
                    var url = Text(button, "url");
                    yield return (new TemplateButtonDto("Url", text, url), url is not null && url.Contains("{{", StringComparison.Ordinal) ? TemplateUnsupportedReasons.DynamicUrlButton : null);
                    break;
                case "PHONE_NUMBER":
                    yield return (new TemplateButtonDto("PhoneNumber", text, null), null);
                    break;
                case "FLOW":
                    yield return (new TemplateButtonDto("Other", text, null), TemplateUnsupportedReasons.Flow);
                    break;
                case "OTP":
                    yield return (new TemplateButtonDto("Other", text, null), TemplateUnsupportedReasons.Authentication);
                    break;
                default:
                    yield return (new TemplateButtonDto("Other", text, null), TemplateUnsupportedReasons.Other);
                    break;
            }
        }
    }

    private static string? Worst(string? current, string? candidate)
    {
        if (candidate is null)
        {
            return current;
        }

        return current is null || Array.IndexOf(ReasonPriority, candidate) < Array.IndexOf(ReasonPriority, current) ? candidate : current;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // «{{first_name}}» o «{{1}}»; Meta no admite espacios en las claves, pero sí tolera los de alrededor.
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

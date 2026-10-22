using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 D-I1: un mensaje interactivo de WhatsApp saliente (botones de respuesta, lista o botón con URL). Es lo que
/// el motor de chatbots manda dentro de la ventana de 24 h; no existe en Instagram.</summary>
public abstract record InteractiveSend(string Body, string? Header, string? Footer);

public sealed record InteractiveButton(string Id, string Title);

public sealed record ButtonsSend(string Body, IReadOnlyList<InteractiveButton> Buttons, string? Header = null, string? Footer = null)
    : InteractiveSend(Body, Header, Footer);

public sealed record ListRow(string Id, string Title, string? Description = null);

public sealed record ListSection(string? Title, IReadOnlyList<ListRow> Rows);

public sealed record ListSend(string Body, string ButtonLabel, IReadOnlyList<ListSection> Sections, string? Header = null, string? Footer = null)
    : InteractiveSend(Body, Header, Footer);

public sealed record CtaUrlSend(string Body, string DisplayText, Uri Url, string? Header = null, string? Footer = null)
    : InteractiveSend(Body, Header, Footer);

/// <summary>Los topes que Meta aplica a cada pieza: pasarse devuelve un 400 de Graph recién al enviar, así que se valida antes.</summary>
public static class InteractiveLimits
{
    public const int BodyMax = 1024, HeaderMax = 60, FooterMax = 60, ButtonsMax = 3, ButtonTitleMax = 20, ButtonIdMax = 256,
        RowsMax = 10, RowIdMax = 200, RowTitleMax = 24, RowDescriptionMax = 72, ListButtonMax = 20, DisplayTextMax = 20;
}

public static class InteractiveSendValidation
{
    /// <summary>Códigos <c>interactive.*</c> de lo que viola los topes; vacío = válido.</summary>
    public static IReadOnlyList<string> Validate(InteractiveSend send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(send.Body))
        {
            errors.Add("interactive.body.empty");
        }
        else if (send.Body.Length > InteractiveLimits.BodyMax)
        {
            errors.Add("interactive.body.too_long");
        }

        if (send.Header is { Length: > InteractiveLimits.HeaderMax })
        {
            errors.Add("interactive.header.too_long");
        }

        if (send.Footer is { Length: > InteractiveLimits.FooterMax })
        {
            errors.Add("interactive.footer.too_long");
        }

        switch (send)
        {
            case ButtonsSend buttons:
                ValidateButtons(buttons, errors);
                break;
            case ListSend list:
                ValidateList(list, errors);
                break;
            case CtaUrlSend cta:
                if (string.IsNullOrWhiteSpace(cta.DisplayText))
                {
                    errors.Add("interactive.cta.display_text_empty");
                }
                else if (cta.DisplayText.Length > InteractiveLimits.DisplayTextMax)
                {
                    errors.Add("interactive.cta.display_text_too_long");
                }

                // Meta sólo abre enlaces https en el botón; http se rechaza en Graph.
                if (!cta.Url.IsAbsoluteUri || cta.Url.Scheme != Uri.UriSchemeHttps)
                {
                    errors.Add("interactive.cta.url_not_https");
                }

                break;
        }

        return errors;
    }

    private static void ValidateButtons(ButtonsSend send, List<string> errors)
    {
        if (send.Buttons.Count == 0)
        {
            errors.Add("interactive.buttons.empty");
        }

        if (send.Buttons.Count > InteractiveLimits.ButtonsMax)
        {
            errors.Add("interactive.buttons.too_many");
        }

        if (send.Buttons.Any(b => string.IsNullOrWhiteSpace(b.Title)))
        {
            errors.Add("interactive.buttons.title_empty");
        }

        if (send.Buttons.Any(b => b.Title.Length > InteractiveLimits.ButtonTitleMax))
        {
            errors.Add("interactive.buttons.title_too_long");
        }

        // El id es lo que vuelve en button_reply: vacío, el motor no sabría qué salida tomar.
        if (send.Buttons.Any(b => string.IsNullOrWhiteSpace(b.Id)))
        {
            errors.Add("interactive.buttons.id_empty");
        }

        if (send.Buttons.Any(b => b.Id.Length > InteractiveLimits.ButtonIdMax))
        {
            errors.Add("interactive.buttons.id_too_long");
        }

        if (send.Buttons.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != send.Buttons.Count)
        {
            errors.Add("interactive.buttons.id_duplicated");
        }
    }

    private static void ValidateList(ListSend send, List<string> errors)
    {
        // El tope de 10 filas es de toda la lista, no de cada sección.
        var rows = send.Sections.SelectMany(s => s.Rows).ToList();
        if (rows.Count == 0)
        {
            errors.Add("interactive.list.no_rows");
        }

        if (rows.Count > InteractiveLimits.RowsMax)
        {
            errors.Add("interactive.list.too_many_rows");
        }

        if (rows.Any(r => string.IsNullOrWhiteSpace(r.Title)))
        {
            errors.Add("interactive.list.row_title_empty");
        }

        if (rows.Any(r => r.Title.Length > InteractiveLimits.RowTitleMax))
        {
            errors.Add("interactive.list.row_title_too_long");
        }

        if (rows.Any(r => string.IsNullOrWhiteSpace(r.Id)))
        {
            errors.Add("interactive.list.id_empty");
        }

        if (rows.Any(r => r.Id.Length > InteractiveLimits.RowIdMax))
        {
            errors.Add("interactive.list.row_id_too_long");
        }

        if (rows.Any(r => r.Description is { Length: > InteractiveLimits.RowDescriptionMax }))
        {
            errors.Add("interactive.list.row_description_too_long");
        }

        if (string.IsNullOrWhiteSpace(send.ButtonLabel))
        {
            errors.Add("interactive.list.button_empty");
        }
        else if (send.ButtonLabel.Length > InteractiveLimits.ListButtonMax)
        {
            errors.Add("interactive.list.button_too_long");
        }

        if (rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count)
        {
            errors.Add("interactive.list.id_duplicated");
        }
    }
}

public static class InteractiveSendJson
{
    /// <summary>El objeto <c>interactive</c> que Meta espera en <c>POST /{phone}/messages</c>. Las claves van en este orden y las
    /// opcionales ausentes no viajan.</summary>
    public static JsonObject ToMetaObject(InteractiveSend send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var type = send switch
        {
            ButtonsSend => "button",
            ListSend => "list",
            CtaUrlSend => "cta_url",
            _ => throw new ArgumentOutOfRangeException(nameof(send), send.GetType().Name, "Tipo interactivo desconocido."),
        };
        var result = new JsonObject { ["type"] = type };
        if (send.Header is not null)
        {
            result["header"] = new JsonObject { ["type"] = "text", ["text"] = send.Header };
        }

        result["body"] = new JsonObject { ["text"] = send.Body };
        if (send.Footer is not null)
        {
            result["footer"] = new JsonObject { ["text"] = send.Footer };
        }

        result["action"] = send switch
        {
            ButtonsSend b => new JsonObject
            {
                ["buttons"] = new JsonArray(b.Buttons.Select(x => (JsonNode)new JsonObject
                {
                    ["type"] = "reply",
                    ["reply"] = new JsonObject { ["id"] = x.Id, ["title"] = x.Title },
                }).ToArray()),
            },
            ListSend l => new JsonObject
            {
                ["button"] = l.ButtonLabel,
                ["sections"] = new JsonArray(l.Sections.Select(SectionObject).ToArray()),
            },
            CtaUrlSend c => new JsonObject
            {
                ["name"] = "cta_url",
                ["parameters"] = new JsonObject { ["display_text"] = c.DisplayText, ["url"] = c.Url.AbsoluteUri },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(send)),
        };
        return result;
    }

    /// <summary>D-I2: el detalle que se guarda del mensaje es el mismo objeto de Meta, serializado.</summary>
    public static string ToDetailsJson(InteractiveSend send) => ToMetaObject(send).ToJsonString(DetailsOptions);

    /// <summary>El detalle se guarda legible: sin escapar las tildes, que el <c>ToJsonString()</c> por defecto vuelve <c>\u00E1</c>.</summary>
    public static readonly JsonSerializerOptions DetailsOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonNode SectionObject(ListSection section)
    {
        var result = new JsonObject();
        if (section.Title is not null)
        {
            result["title"] = section.Title;
        }

        result["rows"] = new JsonArray(section.Rows.Select(r =>
        {
            var row = new JsonObject { ["id"] = r.Id, ["title"] = r.Title };
            if (r.Description is not null)
            {
                row["description"] = r.Description;
            }

            return (JsonNode)row;
        }).ToArray());
        return result;
    }
}

/// <summary>Spec 2026-10-21 D-I2: el camino de vuelta de <see cref="InteractiveSendJson.ToMetaObject"/>, para el hilo. El backend
/// arma lo que la pantalla pinta (regla BFF): el frontend no tiene por qué conocer la forma del objeto de Meta guardado en
/// <c>details</c>. Cualquier forma que no se reconozca da <c>null</c>: un detalle raro deja el mensaje sin botones, nunca rompe la página.</summary>
internal static class InteractiveDetailsJson
{
    public static InteractiveDto? Parse(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryObject(root, "action", out var action))
            {
                return null;
            }

            return StringOf(root, "type") switch
            {
                "button" => Buttons(action) is { } buttons ? new InteractiveDto("button", buttons, null, null) : null,
                "list" => List(action) is { } list ? new InteractiveDto("list", null, list, null) : null,
                "cta_url" => Cta(action) is { } cta ? new InteractiveDto("cta_url", null, null, cta) : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<InteractiveButtonDto>? Buttons(JsonElement action)
    {
        if (!TryArray(action, "buttons", out var items))
        {
            return null;
        }

        var result = new List<InteractiveButtonDto>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryObject(item, "reply", out var reply)
                || StringOf(reply, "id") is not { } id || StringOf(reply, "title") is not { } title)
            {
                return null;
            }

            result.Add(new InteractiveButtonDto(id, title));
        }

        return result;
    }

    private static InteractiveListDto? List(JsonElement action)
    {
        if (StringOf(action, "button") is not { } label || !TryArray(action, "sections", out var sections))
        {
            return null;
        }

        var result = new List<InteractiveListSectionDto>();
        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object || !TryArray(section, "rows", out var rows))
            {
                return null;
            }

            var parsed = new List<InteractiveListRowDto>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || StringOf(row, "id") is not { } id || StringOf(row, "title") is not { } title)
                {
                    return null;
                }

                parsed.Add(new InteractiveListRowDto(id, title, StringOf(row, "description")));
            }

            result.Add(new InteractiveListSectionDto(StringOf(section, "title"), parsed));
        }

        return new InteractiveListDto(label, result);
    }

    private static InteractiveCtaDto? Cta(JsonElement action) =>
        TryObject(action, "parameters", out var parameters)
        && StringOf(parameters, "display_text") is { } displayText && StringOf(parameters, "url") is { } url
            ? new InteractiveCtaDto(displayText, url)
            : null;

    private static string? StringOf(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool TryObject(JsonElement value, string name, out JsonElement result) =>
        value.TryGetProperty(name, out result) && result.ValueKind == JsonValueKind.Object;

    private static bool TryArray(JsonElement value, string name, out JsonElement result) =>
        value.TryGetProperty(name, out result) && result.ValueKind == JsonValueKind.Array;
}

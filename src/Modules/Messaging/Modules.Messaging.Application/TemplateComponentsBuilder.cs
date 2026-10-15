using System.Text.Json.Nodes;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-14 §4.7: un botón del cuerpo de crear. <c>Type</c> es <c>QuickReply</c>, <c>Url</c> o <c>PhoneNumber</c>, los
/// mismos nombres que <see cref="TemplateButtonDto"/>. Todo opcional: lo que falte lo marca el validador con su clave.</summary>
public sealed record TemplateButtonInput(string? Type, string? Text, string? Url, string? PhoneNumber);

/// <summary>§4.4: una plantilla ya validada, lista para armar su payload. <c>Examples</c> trae exactamente las variables del cuerpo.</summary>
public sealed record TemplateDraft(
    string Name,
    string Language,
    string Category,
    string? Header,
    string Body,
    string? Footer,
    IReadOnlyList<TemplateButtonInput> Buttons,
    IReadOnlyDictionary<string, string> Examples);

/// <summary>
/// Spec 2026-10-14 §4.4 «Crear»: el payload de <c>POST /{waba}/message_templates</c>. Variables con nombre (D-T9) con
/// <c>body_text_named_params</c> en orden de aparición; encabezado de texto o de documento (<c>header_handle</c>, §4.5); pie; botones
/// con el nombre de Meta. Sin <c>allow_category_change</c> (D-T10). Puro: lo usan la creación libre y la de cotización.
/// </summary>
public static class TemplateComponentsBuilder
{
    public const string Named = "NAMED";

    /// <summary>Las variables del cuerpo en orden de aparición, sin repetir. El regex es el de <see cref="TemplateCatalog"/>; la forma exacta la exige el validador.</summary>
    public static IReadOnlyList<string> VariablesOf(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return TemplateCatalog.Placeholder().Matches(body).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static JsonObject CreatePayload(TemplateDraft draft, string? documentHeaderHandle = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new JsonObject
        {
            ["name"] = draft.Name,
            ["language"] = draft.Language,
            ["category"] = draft.Category,
            ["parameter_format"] = Named,
            ["components"] = Components(draft, documentHeaderHandle),
        };
    }

    public static JsonArray Components(TemplateDraft draft, string? documentHeaderHandle = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var components = new JsonArray();
        if (documentHeaderHandle is not null)
        {
            components.Add(new JsonObject
            {
                ["type"] = "HEADER",
                ["format"] = "DOCUMENT",
                ["example"] = new JsonObject { ["header_handle"] = new JsonArray(documentHeaderHandle) },
            });
        }
        else if (!string.IsNullOrEmpty(draft.Header))
        {
            components.Add(new JsonObject { ["type"] = "HEADER", ["format"] = "TEXT", ["text"] = draft.Header });
        }

        var body = new JsonObject { ["type"] = "BODY", ["text"] = draft.Body };
        var variables = VariablesOf(draft.Body);
        if (variables.Count > 0)
        {
            var examples = new JsonArray();
            foreach (var variable in variables)
            {
                examples.Add(new JsonObject
                {
                    ["param_name"] = variable,
                    ["example"] = draft.Examples.TryGetValue(variable, out var example) ? example : string.Empty,
                });
            }

            body["example"] = new JsonObject { ["body_text_named_params"] = examples };
        }

        components.Add(body);
        if (!string.IsNullOrEmpty(draft.Footer))
        {
            components.Add(new JsonObject { ["type"] = "FOOTER", ["text"] = draft.Footer });
        }

        if (draft.Buttons.Count > 0)
        {
            var buttons = new JsonArray();
            foreach (var button in draft.Buttons)
            {
                var item = new JsonObject { ["text"] = button.Text };
                switch (button.Type)
                {
                    case "Url":
                        item["type"] = "URL";
                        item["url"] = button.Url;
                        break;
                    case "PhoneNumber":
                        item["type"] = "PHONE_NUMBER";
                        item["phone_number"] = button.PhoneNumber;
                        break;
                    default:
                        item["type"] = "QUICK_REPLY";
                        break;
                }

                buttons.Add(item);
            }

            components.Add(new JsonObject { ["type"] = "BUTTONS", ["buttons"] = buttons });
        }

        return components;
    }
}

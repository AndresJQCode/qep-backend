using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Modules.Automations.Domain.Flow;

/// <summary>
/// El JSON no es un flujo. Los códigos son <c>flow.unknown_kind</c> (un <c>kind</c> que no es ninguno de §5.4) y
/// <c>flow.malformed</c> (todo lo demás: JSON inválido, <c>schema</c> distinto de 1, un campo con el tipo equivocado, un valor
/// fuera de su lista cerrada, un id de bloque que falta, repetido o con otra forma). Lo que tiene forma de flujo pero no se
/// puede publicar no es esto: es un <see cref="FlowIssue"/> de <see cref="FlowValidator"/>.
/// </summary>
public sealed class FlowFormatException(string code, string message) : Exception(message)
{
    public const string UnknownKind = "flow.unknown_kind";
    public const string Malformed = "flow.malformed";

    public string Code { get; } = code;
}

/// <summary>
/// Spec 2026-10-21 §4.3 y Anexo A: lee y escribe el flujo con <c>System.Text.Json</c>, en camelCase y polimórfico por
/// <c>kind</c> (<see cref="FlowNodeJsonConverter"/>).
/// <para>
/// D-C3: un borrador a medio hacer siempre se guarda. Por eso todo lo que el admin escribe (textos, títulos, etiquetas,
/// <c>saveTo</c>, ruta, query, mapeo, la variable de una regla, los ids de botón y fila) se lee aunque falte o venga
/// <c>null</c> (como <c>""</c>), y lo marca <see cref="FlowValidator"/>. Sólo se rechaza al leer lo que el editor arma solo:
/// los ids de bloque y los campos de lista cerrada (<c>kind</c>, <c>method</c>, <c>operator</c>, <c>match</c>, <c>accept</c>,
/// <c>validation.kind</c>, <c>options.kind</c>, <c>body.kind</c>, <c>fallback</c>). El editor siempre manda un valor por
/// defecto en esos campos, así que uno que falte o no esté en la lista es un error del cliente, no del admin.
/// </para>
/// </summary>
public static partial class FlowJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new FlowNodeJsonConverter() },
    };

    // El flujo viaja a la base (jsonb) y al editor, nunca a una página HTML: no hace falta escapar tildes ni comillas.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static FlowDefinition Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new FlowFormatException(FlowFormatException.Malformed, $"El flujo no es un JSON válido: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            const string where = "el flujo";
            JsonFields.RequireObject(root, where);
            var schema = JsonFields.RequiredInt(root, "schema", where);
            if (schema != FlowDefinition.CurrentSchema)
            {
                throw JsonFields.Malformed($"El flujo es de la versión {schema}, y sólo existe la {FlowDefinition.CurrentSchema}.");
            }

            var start = JsonFields.RequiredObject(root, "start", where);
            var variables = JsonFields.OptionalArray(root, "variables", where, ReadVariable) ?? [];
            var nodes = JsonFields.RequiredArray(root, "nodes", where, element => element.Deserialize<FlowNode>(Options) ?? throw JsonFields.Malformed("Un bloque del flujo es null."));

            var repeated = nodes.GroupBy(node => node.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (repeated is not null)
            {
                throw JsonFields.Malformed($"Hay dos bloques con el id «{repeated.Key}».");
            }

            return new FlowDefinition(schema, new FlowStart(JsonFields.OptionalString(start, "firstNodeId", "el inicio")), variables, nodes);
        }
    }

    public static string Serialize(FlowDefinition flow)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", flow.Schema);
            writer.WriteStartObject("start");
            writer.WriteString("firstNodeId", flow.Start.FirstNodeId);
            writer.WriteEndObject();
            writer.WriteStartArray("variables");
            foreach (var variable in flow.Variables)
            {
                writer.WriteStartObject();
                writer.WriteString("name", variable.Name);
                writer.WriteString("type", variable.Type);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("nodes");
            JsonSerializer.Serialize(writer, flow.Nodes, Options);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static FlowVariable ReadVariable(JsonElement element)
    {
        const string where = "una variable";
        JsonFields.RequireObject(element, where);
        return new FlowVariable(
            JsonFields.Text(element, "name", where),
            JsonFields.RequiredOneOf(element, "type", where, FlowVariableTypes.All));
    }

    // Anexo A: la forma de los ids que arma el editor.
    [GeneratedRegex(@"^[a-z0-9-]{1,64}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex NodeId();

}

/// <summary>El bloque según su <c>kind</c>. Lee de un <see cref="JsonElement"/> para mirar <c>kind</c> antes que el resto.</summary>
internal sealed class FlowNodeJsonConverter : JsonConverter<FlowNode>
{
    public override FlowNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ReadNode(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, FlowNode value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("kind", value.Kind);
        writer.WriteString("title", value.Title);
        switch (value)
        {
            case MessageNode message:
                writer.WriteString("text", message.Text);
                WriteNullable(writer, "urlButton", message.UrlButton, (w, button) =>
                {
                    w.WriteString("label", button.Label);
                    w.WriteString("url", button.Url);
                });
                break;
            case OpenQuestionNode question:
                writer.WriteString("text", question.Text);
                writer.WriteString("saveTo", question.SaveTo);
                writer.WriteStartObject("validation");
                writer.WriteString("kind", question.Validation.Kind);
                writer.WriteString("pattern", question.Validation.Pattern);
                WriteStrings(writer, "keywords", question.Validation.Keywords);
                writer.WriteEndObject();
                WriteNumber(writer, "maxAttempts", question.MaxAttempts);
                break;
            case ButtonsNode buttons:
                writer.WriteString("text", buttons.Text);
                writer.WriteStartArray("buttons");
                foreach (var button in buttons.Buttons)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", button.Id);
                    writer.WriteString("label", button.Label);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteString("saveTo", buttons.SaveTo);
                writer.WriteString("fallback", buttons.Fallback);
                WriteGuid(writer, "fallbackMemberId", buttons.FallbackMemberId);
                break;
            case ListNode list:
                writer.WriteString("text", list.Text);
                writer.WriteString("buttonLabel", list.ButtonLabel);
                writer.WriteStartObject("options");
                writer.WriteString("kind", list.Options.Kind);
                if (list.Options.Fixed is null)
                {
                    writer.WriteNull("fixed");
                }
                else
                {
                    writer.WriteStartArray("fixed");
                    foreach (var row in list.Options.Fixed)
                    {
                        WriteRow(writer, row);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteString("collection", list.Options.Collection);
                writer.WriteString("idField", list.Options.IdField);
                writer.WriteString("titleField", list.Options.TitleField);
                writer.WriteString("descriptionField", list.Options.DescriptionField);
                if (list.Options.ExtraOption is null)
                {
                    writer.WriteNull("extraOption");
                }
                else
                {
                    writer.WritePropertyName("extraOption");
                    WriteRow(writer, list.Options.ExtraOption);
                }

                writer.WriteEndObject();
                writer.WriteString("saveTo", list.SaveTo);
                writer.WriteString("fallback", list.Fallback);
                WriteGuid(writer, "fallbackMemberId", list.FallbackMemberId);
                break;
            case AttachmentNode attachment:
                writer.WriteString("text", attachment.Text);
                writer.WriteString("accept", attachment.Accept);
                writer.WriteString("saveTo", attachment.SaveTo);
                break;
            case ConditionNode condition:
                writer.WriteString("match", condition.Match);
                writer.WriteStartArray("rules");
                foreach (var rule in condition.Rules)
                {
                    writer.WriteStartObject();
                    writer.WriteString("variable", rule.Variable);
                    writer.WriteString("operator", rule.Operator);
                    writer.WriteString("value", rule.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            case BusinessHoursNode:
                break;
            case HttpNode http:
                WriteGuid(writer, "connectionId", http.ConnectionId);
                writer.WriteString("method", http.Method);
                writer.WriteString("path", http.Path);
                WritePairs(writer, "query", http.Query);
                WriteNullable(writer, "body", http.Body, (w, body) =>
                {
                    w.WriteString("kind", body.Kind);
                    w.WriteString("template", body.Template);
                    WritePairs(w, "fields", body.Fields);
                    w.WriteString("fileField", body.FileField);
                    w.WriteString("fileVariable", body.FileVariable);
                });
                writer.WriteNumber("timeoutSeconds", http.TimeoutSeconds);
                if (http.OkWhen is null)
                {
                    writer.WriteNull("okWhen");
                }
                else
                {
                    writer.WriteStartArray("okWhen");
                    foreach (var status in http.OkWhen)
                    {
                        writer.WriteNumberValue(status);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteStartArray("mapping");
                foreach (var mapping in http.Mapping)
                {
                    writer.WriteStartObject();
                    writer.WriteString("from", mapping.From);
                    writer.WriteString("to", mapping.To);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("sampleResponse");
                if (http.SampleResponse is { } sample)
                {
                    sample.WriteTo(writer);
                }
                else
                {
                    writer.WriteNullValue();
                }

                break;
            case HandoffNode handoff:
                writer.WriteString("text", handoff.Text);
                WriteGuid(writer, "memberId", handoff.MemberId);
                break;
            case DataConsentNode consent:
                writer.WriteString("text", consent.Text);
                writer.WriteString("onReject", consent.OnReject);
                writer.WriteNumber("maxAsks", consent.MaxAsks);
                WriteGuid(writer, "onRejectMemberId", consent.OnRejectMemberId);
                break;
            case ResolveNode resolve:
                writer.WriteString("text", resolve.Text);
                WriteGuid(writer, "reasonId", resolve.ReasonId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Bloque sin escritura definida.");
        }

        writer.WriteStartObject("outputs");
        foreach (var (output, target) in value.Outputs)
        {
            writer.WriteString(output, target);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static FlowNode ReadNode(JsonElement element)
    {
        JsonFields.RequireObject(element, "un bloque");

        // El kind va primero: de un tipo que no existe no tiene sentido quejarse por su título o sus salidas.
        var kind = JsonFields.RequiredString(element, "kind", "un bloque");
        if (!FlowNodeKinds.All.Contains(kind))
        {
            throw new FlowFormatException(FlowFormatException.UnknownKind, $"Un bloque es de un tipo que no existe: «{kind}».");
        }

        var id = JsonFields.RequiredString(element, "id", "un bloque");
        if (!FlowJson.NodeId().IsMatch(id))
        {
            throw JsonFields.Malformed($"El id de bloque «{id}» no tiene la forma ^[a-z0-9-]{{1,64}}$.");
        }

        var where = $"el bloque {id}";
        var title = JsonFields.Text(element, "title", where);
        var outputs = ReadOutputs(element, where);

        return kind switch
        {
            FlowNodeKinds.Message => new MessageNode(
                id, title, outputs, JsonFields.Text(element, "text", where), ReadUrlButton(element, where)),
            FlowNodeKinds.OpenQuestion => new OpenQuestionNode(
                id, title, outputs, JsonFields.Text(element, "text", where), JsonFields.Text(element, "saveTo", where),
                ReadValidation(element, where), JsonFields.OptionalInt(element, "maxAttempts", where)),
            FlowNodeKinds.Buttons => new ButtonsNode(
                id, title, outputs, JsonFields.Text(element, "text", where), ReadButtons(element, where),
                JsonFields.OptionalString(element, "saveTo", where), JsonFields.RequiredOneOf(element, "fallback", where, InteractiveFallbacks.All),
                JsonFields.OptionalGuid(element, "fallbackMemberId", where)),
            FlowNodeKinds.List => new ListNode(
                id, title, outputs, JsonFields.Text(element, "text", where), JsonFields.Text(element, "buttonLabel", where),
                ReadListOptions(element, where), JsonFields.Text(element, "saveTo", where), JsonFields.RequiredOneOf(element, "fallback", where, InteractiveFallbacks.All),
                JsonFields.OptionalGuid(element, "fallbackMemberId", where)),
            FlowNodeKinds.Attachment => new AttachmentNode(
                id, title, outputs, JsonFields.Text(element, "text", where),
                JsonFields.RequiredOneOf(element, "accept", where, AttachmentAccepts.All), JsonFields.Text(element, "saveTo", where)),
            FlowNodeKinds.Condition => new ConditionNode(
                id, title, outputs, JsonFields.RequiredOneOf(element, "match", where, ConditionMatches.All),
                JsonFields.RequiredArray(element, "rules", where, rule => ReadRule(rule, where))),
            FlowNodeKinds.BusinessHours => new BusinessHoursNode(id, title, outputs),
            FlowNodeKinds.Http => ReadHttp(element, id, title, outputs, where),
            FlowNodeKinds.Handoff => new HandoffNode(
                id, title, outputs, JsonFields.OptionalString(element, "text", where), JsonFields.OptionalGuid(element, "memberId", where)),
            FlowNodeKinds.DataConsent => new DataConsentNode(
                id, title, outputs, JsonFields.Text(element, "text", where),
                // Un bloque guardado antes de R-1 no trae las tres claves y se lee como hoy: sale por «No acepto».
                JsonFields.OptionalOneOf(element, "onReject", where, DataConsentRejectActions.All) ?? DataConsentRejectActions.RejectedOutput,
                JsonFields.OptionalInt(element, "maxAsks", where) ?? DataConsentAsks.Default,
                JsonFields.OptionalGuid(element, "onRejectMemberId", where)),
            FlowNodeKinds.Resolve => new ResolveNode(
                id, title, outputs, JsonFields.OptionalString(element, "text", where), JsonFields.OptionalGuid(element, "reasonId", where)),
            _ => throw new ArgumentOutOfRangeException(nameof(element), kind, "Tipo de bloque sin lectura definida."),
        };
    }

    // Anexo A: los terminales no traen outputs; un bloque sin outputs se lee con todas sus salidas sin conectar.
    private static Dictionary<string, string?> ReadOutputs(JsonElement element, string where)
    {
        var outputs = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!element.TryGetProperty("outputs", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return outputs;
        }

        JsonFields.RequireObject(value, $"las salidas {JsonFields.Of(where)}");
        foreach (var output in value.EnumerateObject())
        {
            outputs[output.Name] = output.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => output.Value.GetString(),
                _ => throw JsonFields.Malformed($"La salida «{output.Name}» {JsonFields.Of(where)} no es un id de bloque."),
            };
        }

        return outputs;
    }

    private static UrlButton? ReadUrlButton(JsonElement element, string where)
    {
        var button = JsonFields.OptionalObject(element, "urlButton", where);
        return button is { } value
            ? new UrlButton(JsonFields.Text(value, "label", $"el botón URL {JsonFields.Of(where)}"), JsonFields.Text(value, "url", $"el botón URL {JsonFields.Of(where)}"))
            : null;
    }

    private static QuestionValidation ReadValidation(JsonElement element, string where)
    {
        var validation = JsonFields.RequiredObject(element, "validation", where);
        var context = $"la validación {JsonFields.Of(where)}";
        return new QuestionValidation(
            JsonFields.RequiredOneOf(validation, "kind", context, QuestionValidationKinds.All),
            JsonFields.OptionalString(validation, "pattern", context),
            JsonFields.OptionalArray(validation, "keywords", context, keyword => JsonFields.StringValue(keyword, context)));
    }

    private static List<FlowButton> ReadButtons(JsonElement element, string where)
    {
        var buttons = JsonFields.RequiredArray(element, "buttons", where, button =>
        {
            JsonFields.RequireObject(button, $"un botón {JsonFields.Of(where)}");
            return new FlowButton(
                JsonFields.Text(button, "id", $"un botón {JsonFields.Of(where)}"), JsonFields.Text(button, "label", $"un botón {JsonFields.Of(where)}"));
        });

        return buttons;
    }

    private static ListOptions ReadListOptions(JsonElement element, string where)
    {
        var options = JsonFields.RequiredObject(element, "options", where);
        var context = $"las opciones {JsonFields.Of(where)}";
        var extra = JsonFields.OptionalObject(options, "extraOption", context) is { } value ? ReadRow(value, context) : null;
        var result = new ListOptions(
            JsonFields.RequiredOneOf(options, "kind", context, ListOptionKinds.All),
            JsonFields.OptionalArray(options, "fixed", context, row => ReadRow(row, context)),
            JsonFields.OptionalString(options, "collection", context),
            JsonFields.OptionalString(options, "idField", context),
            JsonFields.OptionalString(options, "titleField", context),
            JsonFields.OptionalString(options, "descriptionField", context),
            extra);

        return result;
    }

    private static ListFixedRow ReadRow(JsonElement row, string where)
    {
        var context = $"una fila {JsonFields.Of(where)}";
        JsonFields.RequireObject(row, context);
        return new ListFixedRow(
            JsonFields.Text(row, "id", context), JsonFields.Text(row, "title", context), JsonFields.OptionalString(row, "description", context));
    }


    private static ConditionRule ReadRule(JsonElement rule, string where)
    {
        var context = $"una regla {JsonFields.Of(where)}";
        JsonFields.RequireObject(rule, context);
        return new ConditionRule(
            JsonFields.Text(rule, "variable", context),
            JsonFields.RequiredOneOf(rule, "operator", context, ConditionOperators.All),
            JsonFields.OptionalString(rule, "value", context));
    }

    private static HttpNode ReadHttp(JsonElement element, string id, string title, Dictionary<string, string?> outputs, string where)
    {
        var body = JsonFields.OptionalObject(element, "body", where) is { } value ? ReadBody(value, where) : null;
        var sample = element.TryGetProperty("sampleResponse", out var response) && response.ValueKind != JsonValueKind.Null
            ? response.Clone()
            : (JsonElement?)null;

        return new HttpNode(
            id, title, outputs,
            JsonFields.OptionalGuid(element, "connectionId", where),
            JsonFields.RequiredOneOf(element, "method", where, HttpMethods.All),
            JsonFields.Text(element, "path", where),
            JsonFields.OptionalArray(element, "query", where, pair => ReadPair(pair, $"la query {JsonFields.Of(where)}")) ?? [],
            body,
            JsonFields.OptionalInt(element, "timeoutSeconds", where) ?? HttpNode.DefaultTimeoutSeconds,
            JsonFields.OptionalArray(element, "okWhen", where, status => JsonFields.IntValue(status, $"okWhen {JsonFields.Of(where)}")),
            JsonFields.OptionalArray(element, "mapping", where, mapping => ReadMapping(mapping, where)) ?? [],
            sample);
    }

    private static HttpBody ReadBody(JsonElement body, string where)
    {
        var context = $"el cuerpo {JsonFields.Of(where)}";
        return new HttpBody(
            JsonFields.RequiredOneOf(body, "kind", context, HttpBodyKinds.All),
            JsonFields.OptionalString(body, "template", context),
            JsonFields.OptionalArray(body, "fields", context, pair => ReadPair(pair, context)),
            JsonFields.OptionalString(body, "fileField", context),
            JsonFields.OptionalString(body, "fileVariable", context));
    }

    private static KeyValue ReadPair(JsonElement pair, string where)
    {
        JsonFields.RequireObject(pair, where);
        return new KeyValue(JsonFields.Text(pair, "key", where), JsonFields.Text(pair, "value", where));
    }

    private static HttpMapping ReadMapping(JsonElement mapping, string where)
    {
        var context = $"el mapeo {JsonFields.Of(where)}";
        JsonFields.RequireObject(mapping, context);
        return new HttpMapping(JsonFields.Text(mapping, "from", context), JsonFields.Text(mapping, "to", context));
    }

    private static void WriteRow(Utf8JsonWriter writer, ListFixedRow row)
    {
        writer.WriteStartObject();
        writer.WriteString("id", row.Id);
        writer.WriteString("title", row.Title);
        writer.WriteString("description", row.Description);
        writer.WriteEndObject();
    }

    private static void WriteNullable<T>(Utf8JsonWriter writer, string name, T? value, Action<Utf8JsonWriter, T> writeBody)
        where T : class
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writeBody(writer, value);
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WritePairs(Utf8JsonWriter writer, string name, IReadOnlyList<KeyValue>? pairs)
    {
        if (pairs is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartArray(name);
        foreach (var pair in pairs)
        {
            writer.WriteStartObject();
            writer.WriteString("key", pair.Key);
            writer.WriteString("value", pair.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } guid)
        {
            writer.WriteString(name, guid);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}

/// <summary>Lecturas estrictas sobre <see cref="JsonElement"/>: cualquier forma inesperada es <c>flow.malformed</c>, nunca un <see cref="InvalidOperationException"/>.</summary>
internal static class JsonFields
{
    public static FlowFormatException Malformed(string message) => new(FlowFormatException.Malformed, message);

    public static void RequireObject(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Malformed($"{Capitalize(where)} no es un objeto JSON.");
        }
    }

    public static JsonElement RequiredObject(JsonElement element, string name, string where) =>
        OptionalObject(element, name, where) ?? throw Missing(name, where);

    public static JsonElement? OptionalObject(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object ? value : throw WrongType(name, where, "un objeto");
    }

    /// <summary>Un texto que el admin escribe: si falta o es null se lee como vacío, y el validador lo marca (D-C3).</summary>
    public static string Text(JsonElement element, string name, string where) => OptionalString(element, name, where) ?? "";

    public static string RequiredString(JsonElement element, string name, string where) =>
        OptionalString(element, name, where) ?? throw Missing(name, where);

    public static string? OptionalString(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw WrongType(name, where, "un texto");
    }

    public static string RequiredOneOf(JsonElement element, string name, string where, IReadOnlySet<string> allowed)
    {
        var value = RequiredString(element, name, where);
        return allowed.Contains(value)
            ? value
            : throw Malformed($"«{name}» {JsonFields.Of(where)} vale «{value}», que no es ninguno de: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
    }

    /// <summary>Como <see cref="RequiredOneOf"/>, pero <c>null</c> si falta o es <c>null</c>.</summary>
    public static string? OptionalOneOf(JsonElement element, string name, string where, IReadOnlySet<string> allowed)
    {
        var value = OptionalString(element, name, where);
        return value is null || allowed.Contains(value)
            ? value
            : throw Malformed($"«{name}» {JsonFields.Of(where)} vale «{value}», que no es ninguno de: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
    }

    public static int RequiredInt(JsonElement element, string name, string where) =>
        OptionalInt(element, name, where) ?? throw Missing(name, where);

    public static int? OptionalInt(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return IntValue(value, $"«{name}» {JsonFields.Of(where)}");
    }

    public static int IntValue(JsonElement value, string where) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw Malformed($"{Capitalize(where)} no es un entero.");

    public static string StringValue(JsonElement value, string where) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Malformed($"Un valor {JsonFields.Of(where)} no es un texto.");

    public static Guid? OptionalGuid(JsonElement element, string name, string where)
    {
        var text = OptionalString(element, name, where);
        if (text is null)
        {
            return null;
        }

        return Guid.TryParse(text, out var guid) ? guid : throw WrongType(name, where, "un id (GUID)");
    }

    public static List<T> RequiredArray<T>(JsonElement element, string name, string where, Func<JsonElement, T> read) =>
        OptionalArray(element, name, where, read) ?? throw Missing(name, where);

    public static List<T>? OptionalArray<T>(JsonElement element, string name, string where, Func<JsonElement, T> read)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw WrongType(name, where, "una lista");
        }

        return [.. value.EnumerateArray().Select(read)];
    }

    /// <summary>«de el bloque n3» no es español: se contrae a «del bloque n3».</summary>
    public static string Of(string where) => where.StartsWith("el ", StringComparison.Ordinal) ? "del " + where[3..] : "de " + where;

    private static FlowFormatException Missing(string name, string where) => Malformed($"Falta «{name}» en {where}.");

    private static FlowFormatException WrongType(string name, string where, string expected) =>
        Malformed($"«{name}» {JsonFields.Of(where)} tiene que ser {expected}.");

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

using System.Collections.Frozen;
using System.Text.Json;

namespace Modules.Automations.Domain.Flow;

/// <summary>
/// Spec 2026-10-21 Anexo A: el flujo de un chatbot, versión 1 (<c>schema</c>). Es lo que guarda el jsonb del borrador y de cada
/// versión publicada; <see cref="FlowJson"/> lo lee y lo escribe, y <see cref="FlowValidator"/> dice si se puede publicar.
/// </summary>
public sealed record FlowDefinition(int Schema, FlowStart Start, IReadOnlyList<FlowVariable> Variables, IReadOnlyList<FlowNode> Nodes)
{
    /// <summary>La única versión del Anexo A que existe; otra no se lee (<c>flow.malformed</c>).</summary>
    public const int CurrentSchema = 1;
}

/// <summary>El bloque Inicio: sólo dice a qué bloque va primero. <c>null</c> mientras el editor no lo conecta.</summary>
public sealed record FlowStart(string? FirstNodeId);

/// <summary>Una variable declarada en el flujo; <see cref="Type"/> es uno de <see cref="FlowVariableTypes"/>.</summary>
public sealed record FlowVariable(string Name, string Type);

/// <summary>
/// Un bloque. <see cref="Outputs"/> va de nombre de salida a id del bloque destino (<c>null</c> = sin conectar); las salidas que
/// cuentan son las de <see cref="FlowNodeKinds.OutputsOf"/>, y una clave de más (un botón borrado) no la sigue nadie.
/// </summary>
public abstract record FlowNode(string Id, string Kind, string Title, IReadOnlyDictionary<string, string?> Outputs);

/// <summary>Con <see cref="UrlButton"/> sale como interactivo <c>CtaUrl</c> (D-I1); sin él, como texto.</summary>
public sealed record MessageNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text, UrlButton? UrlButton)
    : FlowNode(Id, FlowNodeKinds.Message, Title, Outputs);

public sealed record UrlButton(string Label, string Url);

/// <summary><see cref="MaxAttempts"/> pisa el del chatbot (D-C7); <c>null</c> usa el del chatbot.</summary>
public sealed record OpenQuestionNode(
    string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text, string SaveTo, QuestionValidation Validation,
    int? MaxAttempts)
    : FlowNode(Id, FlowNodeKinds.OpenQuestion, Title, Outputs);

/// <summary><see cref="Kind"/> es uno de <see cref="QuestionValidationKinds"/>; <see cref="Pattern"/> va con <c>regex</c> y <see cref="Keywords"/> con <c>keywords</c>.</summary>
public sealed record QuestionValidation(string Kind, string? Pattern, IReadOnlyList<string>? Keywords);

public sealed record ButtonsNode(
    string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text, IReadOnlyList<FlowButton> Buttons, string? SaveTo,
    string Fallback)
    : FlowNode(Id, FlowNodeKinds.Buttons, Title, Outputs);

/// <summary><see cref="Id"/> es también el nombre de la salida del botón.</summary>
public sealed record FlowButton(string Id, string Label);

public sealed record ListNode(
    string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text, string ButtonLabel, ListOptions Options,
    string SaveTo, string Fallback)
    : FlowNode(Id, FlowNodeKinds.List, Title, Outputs);

/// <summary>
/// <see cref="Kind"/> es uno de <see cref="ListOptionKinds"/>: <c>fixed</c> usa <see cref="Fixed"/>; <c>dynamic</c> arma las filas desde
/// la variable <see cref="Collection"/> con los campos <see cref="IdField"/>, <see cref="TitleField"/> y <see cref="DescriptionField"/>.
/// <see cref="ExtraOption"/> agrega una fila fija al final en los dos casos.
/// </summary>
public sealed record ListOptions(
    string Kind, IReadOnlyList<ListFixedRow>? Fixed, string? Collection, string? IdField, string? TitleField, string? DescriptionField,
    ListFixedRow? ExtraOption);

public sealed record ListFixedRow(string Id, string Title, string? Description);

/// <summary><see cref="Accept"/> es uno de <see cref="AttachmentAccepts"/>.</summary>
public sealed record AttachmentNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text, string Accept, string SaveTo)
    : FlowNode(Id, FlowNodeKinds.Attachment, Title, Outputs);

/// <summary><see cref="Match"/> es uno de <see cref="ConditionMatches"/>.</summary>
public sealed record ConditionNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Match, IReadOnlyList<ConditionRule> Rules)
    : FlowNode(Id, FlowNodeKinds.Condition, Title, Outputs);

/// <summary><see cref="Variable"/> es una referencia sin llaves (<c>cliente</c>, <c>cliente.id</c>); <see cref="Operator"/>, uno de <see cref="ConditionOperators"/>.</summary>
public sealed record ConditionRule(string Variable, string Operator, string? Value);

public sealed record BusinessHoursNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs)
    : FlowNode(Id, FlowNodeKinds.BusinessHours, Title, Outputs);

/// <summary>
/// D-H4. <see cref="ConnectionId"/> es <c>null</c> en un flujo instalado de plantilla hasta que el admin elige su conexión (D-C14).
/// <see cref="OkWhen"/> <c>null</c> = cualquier 2xx. <see cref="SampleResponse"/> es lo que el simulador usa en vez de llamar a la API.
/// </summary>
public sealed record HttpNode(
    string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, Guid? ConnectionId, string Method, string Path,
    IReadOnlyList<KeyValue> Query, HttpBody? Body, int TimeoutSeconds, IReadOnlyList<int>? OkWhen, IReadOnlyList<HttpMapping> Mapping,
    JsonElement? SampleResponse)
    : FlowNode(Id, FlowNodeKinds.Http, Title, Outputs)
{
    /// <summary>D-H4: el timeout cuando el bloque no trae uno.</summary>
    public const int DefaultTimeoutSeconds = 8;
}

public sealed record KeyValue(string Key, string Value);

/// <summary>
/// <see cref="Kind"/> es uno de <see cref="HttpBodyKinds"/>: <c>json</c> usa <see cref="Template"/>; <c>multipart</c>, <see cref="Fields"/> y
/// un archivo (<see cref="FileField"/>) ligado a una variable de adjunto (<see cref="FileVariable"/>).
/// </summary>
public sealed record HttpBody(string Kind, string? Template, IReadOnlyList<KeyValue>? Fields, string? FileField, string? FileVariable);

/// <summary><see cref="From"/> es una ruta por puntos e índices sobre la respuesta (<c>data[0]</c>); <see cref="To"/>, la variable que escribe.</summary>
public sealed record HttpMapping(string From, string To);

public sealed record HandoffNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string? Text)
    : FlowNode(Id, FlowNodeKinds.Handoff, Title, Outputs);

public sealed record ResolveNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string? Text, Guid? ReasonId)
    : FlowNode(Id, FlowNodeKinds.Resolve, Title, Outputs);

/// <summary>Brief de la tarea 11 y §5.4: los valores cerrados de cada campo. Un valor fuera de estos no es un flujo (<c>flow.malformed</c>).</summary>
public static class FlowVariableTypes
{
    public static readonly IReadOnlySet<string> All = new[] { "text", "number", "list", "object", "attachment" }.ToFrozenSet(StringComparer.Ordinal);
}

public static class QuestionValidationKinds
{
    public const string None = "none";
    public const string Digits = "digits";
    public const string Number = "number";
    public const string Regex = "regex";
    public const string Keywords = "keywords";

    public static readonly IReadOnlySet<string> All = new[] { None, Digits, Number, Regex, Keywords }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// Qué hacen Botones y Lista ante una respuesta que no es una opción. §5.4 sólo describe <c>repeat</c>; los otros dos son los
/// que manda el editor (qep-frontend, <c>features/automations/types/automations.ts</c>) y el ruling P2-R9 fija su semántica en el motor.
/// </summary>
public static class InteractiveFallbacks
{
    /// <summary>Reenvía el mensaje hasta agotar los intentos y luego aplica D-M7.</summary>
    public const string Repeat = "repeat";

    /// <summary>Sale por <c>other</c> en la primera respuesta inválida si está conectada; si no, como <see cref="Repeat"/>.</summary>
    public const string OtherOutput = "otherOutput";

    /// <summary>Pasa a un asesor en la primera respuesta inválida.</summary>
    public const string Handoff = "handoff";

    public static readonly IReadOnlySet<string> All = new[] { Repeat, OtherOutput, Handoff }.ToFrozenSet(StringComparer.Ordinal);
}

public static class ListOptionKinds
{
    public const string Fixed = "fixed";
    public const string Dynamic = "dynamic";

    public static readonly IReadOnlySet<string> All = new[] { Fixed, Dynamic }.ToFrozenSet(StringComparer.Ordinal);
}

public static class AttachmentAccepts
{
    public const string Image = "image";
    public const string Document = "document";
    public const string Any = "any";

    public static readonly IReadOnlySet<string> All = new[] { Image, Document, Any }.ToFrozenSet(StringComparer.Ordinal);
}

public static class ConditionMatches
{
    public static readonly IReadOnlySet<string> All = new[] { "all", "any" }.ToFrozenSet(StringComparer.Ordinal);
}

public static class ConditionOperators
{
    public static readonly IReadOnlySet<string> All = new[]
    {
        "isEmpty", "isNotEmpty", "equals", "notEquals", "contains", "greaterThan", "lessThan", "countEquals", "countGreaterThan",
    }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>D-H4: sin <c>DELETE</c> ni <c>HEAD</c>.</summary>
public static class HttpMethods
{
    public static readonly IReadOnlySet<string> All = new[] { "GET", "POST", "PUT", "PATCH" }.ToFrozenSet(StringComparer.Ordinal);
}

public static class HttpBodyKinds
{
    public const string Json = "json";
    public const string Multipart = "multipart";

    public static readonly IReadOnlySet<string> All = new[] { Json, Multipart }.ToFrozenSet(StringComparer.Ordinal);
}

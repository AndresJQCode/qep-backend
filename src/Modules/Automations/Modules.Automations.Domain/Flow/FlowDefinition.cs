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
/// cuentan son las de <see cref="FlowNodeKinds.OutputsOf"/>, y una clave de más (un botón borrado, o <c>other</c> con un
/// <c>fallback</c> distinto de <c>otherOutput</c>, spec 2026-10-25 D-3) no la sigue nadie.
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
    string Fallback, Guid? FallbackMemberId = null)
    : FlowNode(Id, FlowNodeKinds.Buttons, Title, Outputs);

/// <summary><see cref="Id"/> es también el nombre de la salida del botón.</summary>
public sealed record FlowButton(string Id, string Label);

public sealed record ListNode(
    string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string Text, string ButtonLabel, ListOptions Options,
    string SaveTo, string Fallback, Guid? FallbackMemberId = null)
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

/// <summary><see cref="MemberId"/> es el destino del traspaso (spec 2026-10-25 D-1); <c>null</c> = sin asignar, que es también lo que
/// significa un flujo guardado antes de que existiera el campo (D-4).</summary>
public sealed record HandoffNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string? Text, Guid? MemberId = null)
    : FlowNode(Id, FlowNodeKinds.Handoff, Title, Outputs);

/// <summary>Spec 2026-10-26 D-1: pide aceptar la política de datos. Los botones son fijos (<see cref="DataConsentButtons"/>) y no se leen
/// del JSON; <see cref="Text"/> se interpola como el de Botones. Spec 2026-10-26 repetir R-1, R-2: <see cref="OnReject"/> es qué
/// hace «No acepto» (por defecto sale por <c>rejected</c>, como antes de existir el modo); <see cref="MaxAsks"/> son las veces, en
/// total, que la persona ve la pregunta y sólo cuenta con <c>repeat</c>; <see cref="OnRejectMemberId"/> es el destino del traspaso
/// (<c>null</c> = sin asignar) y sólo cuenta con <c>repeat</c> y <c>handoff</c>.</summary>
public sealed record DataConsentNode(
    string Id,
    string Title,
    IReadOnlyDictionary<string, string?> Outputs,
    string Text,
    string OnReject = DataConsentRejectActions.RejectedOutput,
    int MaxAsks = DataConsentAsks.Default,
    Guid? OnRejectMemberId = null)
    : FlowNode(Id, FlowNodeKinds.DataConsent, Title, Outputs);

public sealed record ResolveNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, string? Text, Guid? ReasonId)
    : FlowNode(Id, FlowNodeKinds.Resolve, Title, Outputs);

/// <summary>Brief de la tarea 11 y §5.4: los valores cerrados de cada campo. Un valor fuera de estos no es un flujo (<c>flow.malformed</c>).</summary>
public static class FlowVariableTypes
{
    public static readonly IReadOnlySet<string> All = new[] { "text", "number", "list", "object", "attachment" }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>D-M8: las variables que el motor siempre llena, sin que un bloque las escriba, en el orden en que el editor las lista. Las lee
/// el validador (son conocidas en todo flujo) y el motor (son lo único que sobrevive a un reinicio): una sola lista para los dos.</summary>
public static class FlowFixedVariables
{
    public static readonly IReadOnlyList<string> Names = ["nombre", "telefono", "empresa", "conexion"];
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

    /// <summary>Sale por <c>other</c> en la primera respuesta inválida; el validador exige la salida conectada (spec 2026-10-25 D-3).</summary>
    public const string OtherOutput = "otherOutput";

    /// <summary>Pasa a un asesor en la primera respuesta inválida.</summary>
    public const string Handoff = "handoff";

    public static readonly IReadOnlySet<string> All = new[] { Repeat, OtherOutput, Handoff }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>Spec 2026-10-26 repetir R-1: qué hace el bloque Política de datos ante «No acepto».</summary>
public static class DataConsentRejectActions
{
    /// <summary>Vuelve a mostrar la pregunta hasta agotar <see cref="DataConsentNode.MaxAsks"/> y entonces pasa a la bandeja.</summary>
    public const string Repeat = "repeat";

    /// <summary>Sale por <c>rejected</c>: lo que hacía el bloque antes del modo, y lo que significa un bloque guardado sin él.</summary>
    public const string RejectedOutput = "rejectedOutput";

    /// <summary>Pasa a la bandeja en el primer «No acepto».</summary>
    public const string Handoff = "handoff";

    public static readonly IReadOnlySet<string> All = new[] { Repeat, RejectedOutput, Handoff }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>Spec 2026-10-26 repetir R-1: cuántas veces, en total y contando la primera, ve la persona la pregunta con <c>repeat</c>.</summary>
public static class DataConsentAsks
{
    public const int Min = 1;
    public const int Max = 5;
    public const int Default = 3;
}

/// <summary>Spec 2026-10-26 D-1: los dos botones del bloque Política de datos; su id es la salida y no cambian por flujo.</summary>
public static class DataConsentButtons
{
    public const string AcceptLabel = "Acepto";
    public const string RejectLabel = "No acepto";

    public static readonly IReadOnlyList<FlowButton> All =
    [
        new FlowButton(FlowOutputs.Accepted, AcceptLabel),
        new FlowButton(FlowOutputs.Rejected, RejectLabel),
    ];
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

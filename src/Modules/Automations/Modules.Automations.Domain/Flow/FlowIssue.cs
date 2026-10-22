namespace Modules.Automations.Domain.Flow;

/// <summary>Un error impide publicar; un aviso se muestra y deja publicar (spec 2026-10-21 §4.3).</summary>
public enum FlowIssueSeverity
{
    Error,
    Warning,
}

/// <summary>Lo que el validador encontró. <see cref="NodeId"/> es <c>null</c> cuando el problema es del flujo y no de un bloque.</summary>
public sealed record FlowIssue(FlowIssueSeverity Severity, string Code, string? NodeId, string Message);

/// <summary>
/// Lo que el validador necesita saber de fuera del flujo: los canales de las conexiones del chatbot (los nombres de
/// <c>ConversationChannel</c>, Modules.Messaging.Domain/MessageEnums.cs), las conexiones «API externa» del tenant según su estado
/// y si todas las conexiones del chatbot tienen horario asignado.
/// </summary>
public sealed record FlowValidationContext(
    IReadOnlySet<string> Channels, IReadOnlySet<Guid> ActiveExternalApis, IReadOnlySet<Guid> PausedExternalApis,
    bool AllConnectionsHaveBusinessHours);

/// <summary>Los códigos de §4.3, más <c>node.output_unconnected_optional</c> del brief de la tarea 11.</summary>
public static class FlowIssueCodes
{
    public const string StartNoFirstNode = "start.no_first_node";
    public const string NodeOutputUnconnected = "node.output_unconnected";

    /// <summary>Aviso: <c>invalid</c>, <c>other</c> y <c>tooMany</c> sin conectar los cubre D-M7 en tiempo de ejecución.</summary>
    public const string NodeOutputUnconnectedOptional = "node.output_unconnected_optional";

    public const string NodeUnknownTarget = "node.unknown_target";
    public const string NodeUnreachable = "node.unreachable";
    public const string ButtonsTooMany = "buttons.too_many";
    public const string ButtonsLabelTooLong = "buttons.label_too_long";
    public const string ListTooManyRows = "list.too_many_rows";
    public const string ListRowTitleTooLong = "list.row_title_too_long";
    public const string ListRowDescriptionTooLong = "list.row_description_too_long";
    public const string ListChannelNotSupported = "list.channel_not_supported";
    public const string TextTooLong = "text.too_long";
    public const string TextEmpty = "text.empty";
    public const string HttpConnectionMissing = "http.connection_missing";
    public const string HttpConnectionPaused = "http.connection_paused";
    public const string HttpBodyNotJson = "http.body_not_json";
    public const string HttpPathInvalid = "http.path_invalid";
    public const string HttpNoSampleResponse = "http.no_sample_response";
    public const string ConditionNoRules = "condition.no_rules";
    public const string VariableNameInvalid = "variable.name_invalid";
    public const string VariableNeverSet = "variable.never_set";
    public const string BusinessHoursNotAssigned = "business_hours.not_assigned";
}

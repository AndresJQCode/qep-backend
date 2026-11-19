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
/// y si todas las conexiones del chatbot tienen horario asignado. <see cref="HandoffMembers"/> son los miembros que hoy pueden responder
/// conversaciones del tenant (spec 2026-10-25 D-5): el destino de un traspaso fuera de ese conjunto es un error. Spec 2026-11-17 §5.2:
/// <see cref="AiEnabled"/> dice si el tenant tiene el módulo <c>ai</c> y <see cref="Agents"/> son sus agentes con su estado; sin ellos, un
/// bloque «Agente IA» no se puede publicar. Spec 2026-11-18: <see cref="MediaFiles"/> son los archivos de los bloques «Mensaje» que resolvieron
/// para este tenant (id → tipo en minúscula); <c>null</c> = ninguno. Un id ausente, o con otro tipo, es <c>message.media_missing</c>.
/// </summary>
public sealed record FlowValidationContext(
    IReadOnlySet<string> Channels, IReadOnlySet<Guid> ActiveExternalApis, IReadOnlySet<Guid> PausedExternalApis,
    bool AllConnectionsHaveBusinessHours, IReadOnlySet<Guid> HandoffMembers, bool AiEnabled = false, IReadOnlyList<FlowAgentRef>? Agents = null,
    IReadOnlyDictionary<Guid, string>? MediaFiles = null);

/// <summary>Los códigos de §4.3, más <c>node.output_unconnected_optional</c>, <c>list.channel_not_supported</c>, <c>buttons.id_invalid</c> y
/// <c>list.row_id_invalid</c> del brief de la tarea 11 y su revisión.</summary>
public static class FlowIssueCodes
{
    public const string StartNoFirstNode = "start.no_first_node";
    public const string NodeOutputUnconnected = "node.output_unconnected";

    /// <summary>Aviso: <c>tooMany</c> sin conectar lo cubre D-M7 en tiempo de ejecución. <c>invalid</c> sin conectar no avisa (spec 2026-10-25 D-8)
    /// y <c>other</c> sin conectar es error (D-3).</summary>
    public const string NodeOutputUnconnectedOptional = "node.output_unconnected_optional";

    public const string NodeUnknownTarget = "node.unknown_target";
    public const string NodeUnreachable = "node.unreachable";
    public const string ButtonsTooMany = "buttons.too_many";
    public const string ButtonsLabelTooLong = "buttons.label_too_long";

    /// <summary>El id de un botón es el nombre de su salida: vacío, con otra forma, repetido o <c>other</c> no se puede seguir.</summary>
    public const string ButtonsIdInvalid = "buttons.id_invalid";

    /// <summary>El id de una fila es lo que se guarda en <c>{saveTo}.id</c>: vacío, con otra forma o repetido no distingue la fila.</summary>
    public const string ListRowIdInvalid = "list.row_id_invalid";
    public const string ListTooManyRows = "list.too_many_rows";
    public const string ListRowTitleTooLong = "list.row_title_too_long";
    public const string ListRowDescriptionTooLong = "list.row_description_too_long";
    public const string ListChannelNotSupported = "list.channel_not_supported";
    public const string TextTooLong = "text.too_long";
    public const string TextEmpty = "text.empty";

    /// <summary>Aviso, nunca error: el texto de Política de datos sin <c>http://</c> ni <c>https://</c> no lleva a la política (spec 2026-10-26 D-3).</summary>
    public const string DataConsentNoLink = "data_consent.no_link";

    /// <summary>Error: «Preguntar hasta» de Política de datos con <c>repeat</c> fuera de <see cref="DataConsentAsks.Min"/>..<see cref="DataConsentAsks.Max"/>
    /// (spec 2026-10-26 repetir R-5). Con otra acción el tope no cuenta y no se revisa.</summary>
    public const string DataConsentMaxAsksInvalid = "data_consent.max_asks_invalid";
    public const string HttpConnectionMissing = "http.connection_missing";
    public const string HttpConnectionPaused = "http.connection_paused";
    public const string HttpBodyNotJson = "http.body_not_json";
    public const string HttpPathInvalid = "http.path_invalid";
    public const string HttpNoSampleResponse = "http.no_sample_response";
    public const string ConditionNoRules = "condition.no_rules";
    public const string VariableNameInvalid = "variable.name_invalid";
    public const string VariableNeverSet = "variable.never_set";
    public const string BusinessHoursNotAssigned = "business_hours.not_assigned";

    // Ruling P2-R8: lo que Meta o el motor no pueden ejecutar y, como las versiones son inmutables, no debe publicarse.
    public const string ButtonsEmpty = "buttons.empty";
    public const string ListNoRows = "list.no_rows";
    public const string ListDynamicIncomplete = "list.dynamic_incomplete";
    public const string MessageUrlInvalid = "message.url_invalid";

    // Spec 2026-11-18: el archivo de un bloque «Mensaje».

    /// <summary>Error: el archivo no existe, no es de este tenant, no se terminó de subir o es de otro tipo.</summary>
    public const string MessageMediaMissing = "message.media_missing";

    /// <summary>Aviso: un audio no lleva pie; el texto sale en un mensaje aparte, después del audio.</summary>
    public const string MessageAudioCaptionSeparate = "message.audio_caption_separate";

    /// <summary>Aviso: un audio no lleva botón; el botón sale en un interactivo aparte, después del audio.</summary>
    public const string MessageAudioButtonSeparate = "message.audio_button_separate";
    public const string HttpTimeoutInvalid = "http.timeout_invalid";
    public const string QuestionMaxAttemptsInvalid = "question.max_attempts_invalid";
    public const string QuestionPatternInvalid = "question.pattern_invalid";
    public const string QuestionKeywordsEmpty = "question.keywords_empty";

    /// <summary>Ruling P2-R36: un <c>mapping.from</c> que el ejecutor no sabe leer (<c>JsonPath.IsValidResponsePath</c>) guardaría siempre
    /// <c>null</c> sin que nada lo avise.</summary>
    public const string HttpMappingPathInvalid = "http.mapping_path_invalid";

    /// <summary>Error: el miembro de un traspaso (o del «Si responde otra cosa» de Botones/Lista) ya no puede responder conversaciones (spec 2026-10-25 D-5).</summary>
    public const string HandoffMemberInvalid = "handoff.member_invalid";

    /// <summary>Error del chatbot, no de un bloque (<c>NodeId</c> nulo): el miembro de la regla de agotamiento ya no puede responder (spec 2026-10-25 D-5).</summary>
    public const string ExhaustedMemberInvalid = "exhausted.member_invalid";

    /// <summary>Es del chatbot y no de un bloque: lo agrega el handler de publicar (<c>NodeId</c> nulo).</summary>
    public const string TriggerKeywordsEmpty = "trigger.keywords_empty";

    // Spec 2026-11-17 (agente IA en chatbots) §5.2. ai_agent.not_first no existe: ningún disparador llega sin entrante y D-B7 cubre el
    // arranque manual en ejecución (plan 2026-11-17, «Discrepancias» #1).

    /// <summary>Error: el bloque no tiene agente, o el agente ya no existe en el tenant.</summary>
    public const string AiAgentMissing = "ai_agent.agent_missing";

    /// <summary>Error: el agente está en borrador.</summary>
    public const string AiAgentNotPublished = "ai_agent.agent_not_published";

    /// <summary>Aviso: el agente está pausado; el bloque saldrá por <c>error</c> mientras siga así (D-B18).</summary>
    public const string AiAgentPaused = "ai_agent.agent_paused";

    public const string AiAgentObjectiveMissing = "ai_agent.objective_missing";

    public const string AiAgentIdleOutOfRange = "ai_agent.idle_out_of_range";

    /// <summary>Error: el tenant no tiene el módulo <c>ai</c> (D-B16).</summary>
    public const string AiAgentModuleDisabled = "ai_agent.module_disabled";

    /// <summary>Error: una conexión del chatbot es de un canal donde el motor no corre (hoy Instagram, OB-2).</summary>
    public const string AiAgentChannelNotSupported = "ai_agent.channel_not_supported";
}

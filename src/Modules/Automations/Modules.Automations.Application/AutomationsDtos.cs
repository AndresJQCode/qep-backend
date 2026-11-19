using System.Text.Json;

namespace Modules.Automations.Application;

// Spec 2026-10-21 §4.4. Los instantes viajan como instante; el frontend los formatea con la zona del tenant.

public sealed record ChatbotConnectionDto(Guid Id, string Name, string Channel, string Handle);

public sealed record ChatbotTodayDto(int Handled, int HandedOff);

/// <summary>Spec 2026-11-17 D-B15 (BFF): los tokens que gastaron este mes, en el reloj del tenant, las sesiones de los bloques «Agente IA» de
/// este chatbot. Lo suma el backend: la lista no puede pedir el consumo de Ai por chatbot (quien la ve no tiene por qué tener
/// <c>ai.agent.read</c>) ni sumar filas en el navegador.</summary>
public sealed record ChatbotAiUsageDto(long InputTokens, long OutputTokens);

/// <param name="AiUsageThisMonth">Spec 2026-11-17 §7: <c>null</c> si ninguna sesión de un bloque «Agente IA» de este chatbot gastó tokens este
/// mes (la columna dice «—»).</param>
public sealed record ChatbotSummaryDto(
    Guid Id,
    string Name,
    string Description,
    string Status,
    string Trigger,
    IReadOnlyList<ChatbotConnectionDto> Connections,
    int? PublishedVersion,
    DateTimeOffset? PublishedAt,
    bool HasDraftChanges,
    ChatbotTodayDto Today,
    long Version,
    ChatbotAiUsageDto? AiUsageThisMonth = null);

public sealed record ChatbotListResponse(IReadOnlyList<ChatbotSummaryDto> Items);

public sealed record ChatbotDraftDto(JsonElement Flow, JsonElement Layout, DateTimeOffset UpdatedAt, Guid UpdatedBy);

public sealed record ChatbotPublishedDto(int Number, DateTimeOffset At);

/// <summary>Spec 2026-11-18: una publicación en el historial. Sin el flujo: el editor sólo necesita reconocerla y restaurarla.
/// <paramref name="PublishedByName"/> es <c>null</c> si el miembro ya no se puede asignar.</summary>
public sealed record ChatbotVersionDto(
    int Number, DateTimeOffset PublishedAt, Guid PublishedBy, string? PublishedByName, bool IsLive, int BlockCount);

public sealed record ChatbotVersionListResponse(IReadOnlyList<ChatbotVersionDto> Items);

public sealed record FlowIssueDto(string Severity, string Code, string? NodeId, string Message);

/// <summary>Quién atiende la conexión (spec 2026-10-29 §4.3): un chatbot o un agente IA, leído de la tabla neutral de Mensajería.</summary>
/// <param name="ChatbotId">El id del bot que la ocupa, sea chatbot o agente. Se llama así por compatibilidad: el formulario anterior lo lee
/// para saber si la conexión es del chatbot abierto, y con un agente sigue funcionando (ningún chatbot tiene ese id). Lo nuevo lee
/// <see cref="Kind"/> e <see cref="Id"/>.</param>
/// <param name="Kind"><c>chatbot</c> o <c>agent</c>.</param>
public sealed record ServedByDto(Guid ChatbotId, string Name, string Kind, Guid Id);

public sealed record SelectableConnectionDto(
    Guid Id, string Name, string Channel, string Handle, string Status, ServedByDto? ServedBy, string? BusinessHoursName);

public sealed record ExternalApiRefDto(Guid Id, string Name, string Status);

/// <summary>Spec 2026-10-25 D-5: un miembro al que el chatbot puede asignar la conversación.</summary>
public sealed record ChatbotMemberDto(Guid Id, string Name);

/// <summary>Spec 2026-11-17 §7: un agente de IA del tenant para el selector del bloque «Agente IA». <c>Status</c> viaja por nombre
/// (<c>Draft</c>, <c>Active</c>, <c>Paused</c>); <c>Provider</c> y <c>Model</c>, los de la versión publicada (o los del borrador).</summary>
public sealed record ChatbotEditorAgentDto(Guid Id, string Name, string Status, string Provider, string Model);

/// <param name="Agents">Spec 2026-11-17 §7 (BFF): todos los agentes del tenant, en cualquier estado, para el selector del bloque «Agente IA»
/// y para pintar «agente pausado». Viaja acá y no en una ruta de Ai porque quien edita un chatbot tiene <c>automations.chatbot.manage</c> y
/// no necesariamente <c>ai.agent.read</c>. Sin el módulo <c>ai</c> llega vacío.</param>
/// <param name="AiEnabled">D-B16: el tenant tiene el módulo <c>ai</c>; sin él el editor no ofrece el bloque.</param>
/// <param name="MediaFiles">Spec 2026-11-18 (BFF): los archivos de los bloques «Mensaje» del borrador que resuelven para el tenant, en una sola
/// lectura. El editor dibuja archivos que no subió en esta sesión (un borrador reabierto, un chatbot duplicado) sin un request por archivo.
/// Nunca <c>null</c>: <c>[]</c> sin archivos o con un borrador que no se puede leer; un id que no resuelve no viaja y el validador lo marca.</param>
public sealed record ChatbotEditorDto(
    ChatbotSummaryDto Chatbot,
    IReadOnlyList<string> Keywords,
    string RestartKeyword,
    int MaxAttempts,
    ChatbotDraftDto Draft,
    ChatbotPublishedDto? Published,
    IReadOnlyList<FlowIssueDto> Issues,
    IReadOnlyList<SelectableConnectionDto> Connections,
    IReadOnlyList<ExternalApiRefDto> ExternalApis,
    IReadOnlyList<string> Variables,
    string ExhaustedAction,
    Guid? ExhaustedMemberId,
    IReadOnlyList<ChatbotMemberDto> Members,
    IReadOnlyList<ChatbotEditorAgentDto> Agents,
    bool AiEnabled,
    IReadOnlyList<ChatbotMediaFileDto> MediaFiles);

public sealed record DraftSavedDto(long Version, DateTimeOffset UpdatedAt, IReadOnlyList<FlowIssueDto> Issues);

/// <summary>D-C9: el archivo de un chatbot. <see cref="Schema"/> es la versión del archivo (hoy 1), no la del flujo. Lleva el borrador,
/// sin conexiones ni estado; <see cref="ExternalApis"/> son las «API externa» que nombran sus bloques HTTP, para que quien importa
/// sepa qué conexiones debe tener (un id que el tenant ya no tiene viaja con nombre vacío y estado <c>Unknown</c>).</summary>
public sealed record ChatbotExportDto(
    int Schema,
    string Name,
    string Description,
    string Trigger,
    IReadOnlyList<string> Keywords,
    string RestartKeyword,
    int MaxAttempts,
    JsonElement Flow,
    JsonElement Layout,
    IReadOnlyList<ExternalApiRefDto> ExternalApis,
    DateTimeOffset ExportedAt,
    string? ExhaustedAction = null,
    Guid? ExhaustedMemberId = null)
{
    public const int CurrentSchema = 1;
}

/// <summary>D-C15: <see cref="Name"/> (si viene) pisa al del archivo; <see cref="ConnectionMap"/> va de la conexión «API externa» del
/// archivo a la del tenant que importa.</summary>
public sealed record ImportChatbotRequest(string? Name, ChatbotExportDto Export, IReadOnlyDictionary<Guid, Guid>? ConnectionMap);

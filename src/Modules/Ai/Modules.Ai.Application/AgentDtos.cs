using Modules.Ai.Domain;

namespace Modules.Ai.Application;

// Spec 2026-10-29 §5: lo que dibujan la lista y el editor de agentes. Backend for frontend: el editor abre con una sola llamada (el
// agente, sus problemas, las conexiones elegibles con quién las ocupa, los miembros para el traspaso y la lista de modelos) y cada escritura
// responde el editor entero, que es lo que la pantalla repinta.

/// <param name="Model">El que atiende hoy: el de la versión publicada si la hay, el del borrador si nunca se publicó.</param>
/// <param name="ModelRetired">D-A12: el modelo del borrador ya no está en la lista de su proveedor; el agente sigue con él hasta que el tenant lo
/// cambie, pero no se puede volver a publicar así.</param>
/// <param name="HasDraftChanges">Hay cambios guardados sin publicar: la lista muestra «Cambios sin publicar» sin pedir el editor.</param>
public sealed record AgentSummaryDto(
    Guid Id,
    string Name,
    string Status,
    AgentModelRef Model,
    bool ModelRetired,
    IReadOnlyList<AgentConnectionSummaryDto> Connections,
    DateTimeOffset? PublishedAt,
    bool HasDraftChanges,
    long Version);

/// <summary>El nombre y el canal viajan con la conexión para que la lista no cruce con <c>GET /connections</c>.</summary>
public sealed record AgentConnectionSummaryDto(Guid Id, string Name, string Channel);

/// <summary>El consumo del mes y la actividad del día (§5) llegan con las tareas que los producen (plan 2026-11-12, Tareas 5 y 9).</summary>
public sealed record AgentListResponse(IReadOnlyList<AgentSummaryDto> Items);

public sealed record AgentDraftDto(AgentConfig Config, DateTimeOffset UpdatedAt, Guid UpdatedBy);

/// <param name="Provider">Plan 2026-11-12 C-17: el proveedor con que atienden las conversaciones nuevas, que puede no ser el del borrador.</param>
public sealed record AgentPublishedDto(int Number, DateTimeOffset At, string Provider, string Model);

/// <param name="Severity"><c>Error</c> impide publicar; <c>Warning</c> sólo avisa. Por nombre, como los demás enums.</param>
/// <param name="Path">La ruta del campo del <c>config</c> (<c>instructions</c>, <c>handoff.memberId</c>) o <c>connections</c>.</param>
public sealed record AgentIssueDto(string Severity, string Code, string Path, string Message);

/// <summary>Quién ocupa una conexión (§4.3): un chatbot o un agente, para que la pantalla ofrezca ir a pausarlo.</summary>
public sealed record AiServedByDto(string Kind, Guid Id, string Name);

/// <param name="BotSupported"><c>false</c> en un canal sin salida de bot (hoy, todo lo que no es WhatsApp): se muestra deshabilitada en vez
/// de desaparecer, para que el tenant sepa por qué no la puede elegir.</param>
public sealed record AiConnectionDto(
    Guid Id, string Name, string Channel, string Handle, string Status, AiServedByDto? ServedBy, bool BotSupported);

/// <summary>Un destino posible del traspaso a un miembro.</summary>
public sealed record AiMemberDto(Guid Id, string Name);

public sealed record AgentEditorDto(
    Guid Id,
    string Name,
    string Status,
    long Version,
    AgentDraftDto Draft,
    AgentPublishedDto? Published,
    bool HasDraftChanges,
    bool ModelRetired,
    IReadOnlyList<AgentIssueDto> Issues,
    IReadOnlyList<Guid> ConnectionIds,
    IReadOnlyList<AiConnectionDto> Connections,
    IReadOnlyList<AiMemberDto> Members,
    AiModelCatalogDto Models);

/// <summary>D-A7: guardar responde la versión nueva (para el siguiente <c>If-Match</c>) y los problemas, sin el editor entero: el
/// borrador lo tiene la pantalla.</summary>
public sealed record AgentDraftSavedDto(long Version, DateTimeOffset UpdatedAt, IReadOnlyList<AgentIssueDto> Issues);

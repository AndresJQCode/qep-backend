using Modules.Ai.Domain;

namespace Modules.Ai.Application;

// Spec 2026-10-29 §5: lo que dibujan la lista y el editor de agentes. Backend for frontend: el editor abre con una sola llamada (el
// agente, sus problemas, las conexiones elegibles con quién las ocupa, los miembros para el traspaso y la lista de modelos) y cada escritura
// responde el editor entero, que es lo que la pantalla repinta.

/// <param name="Model">El que atiende hoy: el de la versión publicada si la hay, el del borrador si nunca se publicó.</param>
/// <param name="ModelRetired">D-A12: el modelo del borrador ya no está en la lista de su proveedor; el agente sigue con él hasta que el tenant lo
/// cambie, pero no se puede volver a publicar así.</param>
/// <param name="HasDraftChanges">Hay cambios guardados sin publicar: la lista muestra «Cambios sin publicar» sin pedir el editor.</param>
/// <param name="UsageThisMonth">Plan 2026-11-12 C-4: los tokens del mes del tenant por proveedor, con caché; vacío si no gastó nada. Incluye
/// el playground, que gasta la misma clave.</param>
/// <param name="Today">Spec §5: lo del día del tenant.</param>
/// <param name="UsedByChatbots">Backend for frontend (spec 2026-11-17 §7, D-B18): los chatbots publicados que usan el agente en un bloque, para
/// que la lista avise antes de borrar o pausar. Viaja acá porque quien ve agentes tiene <c>ai.agent.read</c> y no necesariamente permiso de
/// chatbots; <c>[]</c> si ninguno.</param>
public sealed record AgentSummaryDto(
    Guid Id,
    string Name,
    string Status,
    AgentModelRef Model,
    bool ModelRetired,
    IReadOnlyList<AgentConnectionSummaryDto> Connections,
    DateTimeOffset? PublishedAt,
    bool HasDraftChanges,
    long Version,
    IReadOnlyList<AgentUsageDto> UsageThisMonth,
    AgentTodayDto Today,
    IReadOnlyList<AiChatbotRef> UsedByChatbots);

/// <summary>El nombre y el canal viajan con la conexión para que la lista no cruce con <c>GET /connections</c>.</summary>
public sealed record AgentConnectionSummaryDto(Guid Id, string Name, string Channel);

/// <param name="UsageThisMonth">El total del mes de todos los agentes por proveedor (D-A27, «el total de la clave»; C-4: por proveedor, porque
/// cada uno es una cuenta y una factura distintas). Lo suma el backend para que el total no dependa de los agentes que filtre la búsqueda.</param>
public sealed record AgentListResponse(IReadOnlyList<AgentSummaryDto> Items, IReadOnlyList<AgentUsageDto> UsageThisMonth);

public sealed record AgentDraftDto(AgentConfig Config, DateTimeOffset UpdatedAt, Guid UpdatedBy);

/// <param name="Provider">Plan 2026-11-12 C-17: el proveedor con que atienden las conversaciones nuevas, que puede no ser el del borrador.</param>
public sealed record AgentPublishedDto(int Number, DateTimeOffset At, string Provider, string Model);

/// <param name="Severity"><c>Error</c> impide publicar; <c>Warning</c> sólo avisa. Por nombre, como los demás enums.</param>
/// <param name="Path">La ruta del campo del <c>config</c> (<c>instructions</c>, <c>handoff.memberId</c>) o <c>connections</c>.</param>
public sealed record AgentIssueDto(string Severity, string Code, string Path, string Message);

/// <summary>Quién ocupa una conexión (§4.3): un chatbot o un agente, para que la pantalla ofrezca ir a pausarlo.</summary>
public sealed record AiServedByDto(string Kind, Guid Id, string Name);

/// <param name="BotSupported"><c>false</c> en un canal sin salida de bot (hoy, todo lo que no es WhatsApp ni Instagram): se muestra deshabilitada en vez
/// de desaparecer, para que el tenant sepa por qué no la puede elegir.</param>
public sealed record AiConnectionDto(
    Guid Id, string Name, string Channel, string Handle, string Status, AiServedByDto? ServedBy, bool BotSupported);

/// <summary>Un destino posible del traspaso a un miembro.</summary>
public sealed record AiMemberDto(Guid Id, string Name);

/// <param name="Members">Los destinos posibles del traspaso a un miembro (pueden responder conversaciones).</param>
/// <param name="QuotationAdvisors">P-4: quién puede quedar como asesor de los borradores de cotización (pueden cotizar). Es otra lista que
/// <paramref name="Members"/>: responder conversaciones y cotizar son permisos distintos, y la pantalla no puede cruzarlos.</param>
/// <param name="SourceTemplateId">La plantilla de la que salió el agente (spec 2026-11-23 menores, M2); <c>null</c> si nació en blanco, duplicado o importado.</param>
/// <param name="SourceTemplateName">El nombre de esa plantilla, resuelto al leer: la pantalla no tiene el catálogo a mano. <c>null</c> si
/// la plantilla ya no existe, y el id se conserva.</param>
/// <param name="QuotationBillingAccounts">Backend for frontend (plan 2026-11-17 A3, F-2): las cuentas de cobro que se pueden elegir para los
/// borradores, las de las empresas activas, en orden. Viajan acá y no por <c>GET /companies</c> porque quien edita un agente tiene
/// <c>ai.agent.manage</c> y no necesariamente <c>companies.company.read</c>, igual que <paramref name="QuotationAdvisors"/>; sin esta lista
/// el agente con <c>create_quotation_draft</c> no se podría publicar desde la pantalla.</param>
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
    AiModelCatalogDto Models,
    IReadOnlyList<AiMemberDto> QuotationAdvisors,
    IReadOnlyList<AiBillingAccountOption> QuotationBillingAccounts,
    AgentSchedulingReadinessDto Scheduling,
    string? SourceTemplateId,
    string? SourceTemplateName);

/// <summary>Spec 2026-11-23 §9.3 y §14: si la Agenda puede atender al agente, para que el editor no ofrezca el interruptor a ciegas.
/// <c>Ready</c> lo calcula el backend: la pantalla no combina los dos campos.</summary>
public sealed record AgentSchedulingReadinessDto(bool ModuleEnabled, int BookableServices, bool Ready);

/// <summary>D-A7: guardar responde la versión nueva (para el siguiente <c>If-Match</c>) y los problemas, sin el editor entero: el
/// borrador lo tiene la pantalla.</summary>
public sealed record AgentDraftSavedDto(long Version, DateTimeOffset UpdatedAt, IReadOnlyList<AgentIssueDto> Issues);

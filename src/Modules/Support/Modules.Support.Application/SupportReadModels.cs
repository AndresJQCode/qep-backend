using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// El listado de la bandeja. Todo filtro nulo o falso no filtra. <paramref name="RunningOnly"/> deja sólo los estados con el reloj
/// corriendo (<c>New</c>, <c>Open</c>, <c>WaitingInternal</c>) y se cruza con <paramref name="Statuses"/>: lo usan «vencidos» y «por
/// vencer» (pre-flight R1). <paramref name="DueBefore"/> es estricto (<c>due_on &lt; DueBefore</c>); <paramref name="DueBetween"/>
/// incluye los dos extremos. La búsqueda acepta el radicado (<paramref name="Number"/>), el texto (<paramref name="SearchTerm"/>,
/// sobre el asunto y los nombres y la razón social del solicitante, no su documento) y los clientes que ya resolvió quien llama
/// (<paramref name="SearchCustomerIds"/>), unidos por O. <paramref name="Priorities"/> deja esos niveles. El orden es por recibido,
/// el más reciente primero; con <paramref name="ByPriority"/>, primero <c>Urgent</c>, <c>High</c>, <c>Medium</c>, <c>Low</c> y dentro de
/// cada nivel por recibido (spec 2026-11-15 §5.3). El id desempata siempre, así la paginación es estable.
/// <paramref name="Kind"/> separa externos de tareas (por defecto externos, como la bandeja de siempre).
/// <paramref name="ParticipantMemberId"/> deja sólo los tickets que esa membresía creó o tiene asignados, en la consulta (spec
/// 2026-11-22 §5.3, §5.6); <paramref name="CreatedByMemberId"/> deja los que creó.
/// </summary>
public sealed record SupportTicketFilter(
    Guid TenantId,
    IReadOnlyCollection<SupportTicketStatus>? Statuses,
    Guid? TypeId,
    Guid? AssigneeMemberId,
    bool UnassignedOnly,
    bool RunningOnly,
    DateOnly? DueBefore,
    (DateOnly From, DateOnly To)? DueBetween,
    long? Number,
    string? SearchTerm,
    IReadOnlyCollection<Guid>? SearchCustomerIds,
    int Page,
    int PageSize,
    IReadOnlyCollection<SupportTicketPriority>? Priorities = null,
    bool ByPriority = false,
    SupportTicketKind Kind = SupportTicketKind.External,
    Guid? ParticipantMemberId = null,
    Guid? CreatedByMemberId = null);

public sealed record SupportTicketPage(IReadOnlyList<SupportTicket> Items, int Total);

/// <summary><paramref name="ByStatus"/> trae los seis estados, también en cero.</summary>
public sealed record SupportInboxTotals(IReadOnlyDictionary<SupportTicketStatus, int> ByStatus, int Overdue, int DueSoon);

using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// El listado de la bandeja. Todo filtro nulo o falso no filtra. <paramref name="RunningOnly"/> deja sólo los estados con el reloj
/// corriendo (<c>New</c>, <c>Open</c>, <c>WaitingInternal</c>) y se cruza con <paramref name="Statuses"/>: lo usan «vencidos» y «por
/// vencer» (pre-flight R1). <paramref name="DueBefore"/> es estricto (<c>due_on &lt; DueBefore</c>); <paramref name="DueBetween"/>
/// incluye los dos extremos. La búsqueda acepta el radicado (<paramref name="Number"/>), el texto (<paramref name="SearchTerm"/>,
/// sobre el asunto y los nombres y la razón social del solicitante, no su documento) y los clientes que ya resolvió quien llama
/// (<paramref name="SearchCustomerIds"/>), unidos por O.
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
    int PageSize);

public sealed record SupportTicketPage(IReadOnlyList<SupportTicket> Items, int Total);

/// <summary><paramref name="ByStatus"/> trae los seis estados, también en cero.</summary>
public sealed record SupportInboxTotals(IReadOnlyDictionary<SupportTicketStatus, int> ByStatus, int Overdue, int DueSoon);

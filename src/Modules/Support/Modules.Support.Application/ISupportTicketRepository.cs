using Modules.Support.Domain;

namespace Modules.Support.Application;

public interface ISupportTicketRepository
{
    /// <summary>Con tracking y sin eventos: los que se agreguen después se insertan al guardar.</summary>
    Task<SupportTicket?> FindAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken);

    /// <summary>La traza del ticket por <c>Ordinal</c>.</summary>
    Task<IReadOnlyList<SupportTicketEvent>> ListEventsAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken);

    /// <summary>Fase 4b (H15): las encuestas del ticket, por número de resolución. Sin tracking: el worker las escribe por su
    /// repositorio.</summary>
    Task<IReadOnlyList<SupportTicketSurvey>> ListSurveysAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken);

    /// <summary>Fase 4b (H20): la encuesta de la última resolución del ticket, sin tracking; null si nunca se resolvió.</summary>
    Task<SupportTicketSurvey?> FindLatestSurveyAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken);

    /// <summary>Fase 4b (H19): la encuesta del token, por el hash (<c>UX_ticket_surveys_token</c>) y filtrada por el tenant del slug, sin
    /// tracking.</summary>
    Task<SupportTicketSurvey?> FindSurveyByTokenHashAsync(Guid tenantId, byte[] tokenHash, CancellationToken cancellationToken);

    Task<SupportTicketPage> ListAsync(SupportTicketFilter filter, CancellationToken cancellationToken);

    /// <summary>Los totales del tenant con el mismo alcance que la lista: el <paramref name="kind"/> y, si no es null, sólo los tickets
    /// que <paramref name="participantMemberId"/> creó o tiene asignados. <paramref name="dueSoonLimit"/> es el último vencimiento que
    /// cuenta como «por vencer»; null cuando el umbral del tenant es 0 (pre-flight R1).</summary>
    Task<SupportInboxTotals> TotalsAsync(
        Guid tenantId,
        SupportTicketKind kind,
        Guid? participantMemberId,
        DateOnly today,
        DateOnly? dueSoonLimit,
        CancellationToken cancellationToken);

    /// <summary>Los tickets abiertos (no <c>Closed</c>) de la conversación, con tracking: los candidatos de un entrante (plan E5).
    /// Cada uno decide con <see cref="SupportTicket.CoversInbound"/> si la hora del mensaje cae en su ventana.</summary>
    Task<IReadOnlyList<SupportTicket>> ListOpenByConversationAsync(
        Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Los de la conversación y, si hay cliente, también los de ese cliente por otros canales, en cualquier estado y sin
    /// tracking: la pestaña de Soporte de la ficha de la conversación.</summary>
    Task<IReadOnlyList<SupportTicket>> ListByConversationOrCustomerAsync(
        Guid tenantId, Guid conversationId, Guid? customerId, CancellationToken cancellationToken);

    /// <summary>The ticket of the tenant filed with that idempotency key (spec 2026-11-15 §9.2, plan L27), without tracking; null if
    /// none. Always filtered by tenant: the same key in another tenant is another ticket.</summary>
    Task<SupportTicket?> FindByClientRequestAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken);

    /// <summary>Fase 4a (plan G4): si algún ticket del tenant, en cualquier estado, usa el tipo. Es la pregunta previa del borrado; la
    /// regla la cierra la FK.</summary>
    Task<bool> AnyOfTypeAsync(Guid tenantId, SupportTicketTypeId typeId, CancellationToken cancellationToken);

    /// <summary>Fase 4a (plan G6): los tipos del tenant que algún ticket usa, para el <c>InUse</c> de la lista.</summary>
    Task<IReadOnlySet<SupportTicketTypeId>> UsedTypeIdsAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(SupportTicket ticket);
}

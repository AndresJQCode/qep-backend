using Modules.Support.Domain;

namespace Modules.Support.Application;

public interface ISupportTicketRepository
{
    /// <summary>Con tracking y sin eventos: los que se agreguen después se insertan al guardar.</summary>
    Task<SupportTicket?> FindAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken);

    /// <summary>La traza del ticket por <c>Ordinal</c>.</summary>
    Task<IReadOnlyList<SupportTicketEvent>> ListEventsAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken);

    Task<SupportTicketPage> ListAsync(SupportTicketFilter filter, CancellationToken cancellationToken);

    /// <summary>Los totales del tenant entero. <paramref name="dueSoonLimit"/> es el último vencimiento que cuenta como «por
    /// vencer»; null cuando el umbral del tenant es 0 (pre-flight R1).</summary>
    Task<SupportInboxTotals> TotalsAsync(Guid tenantId, DateOnly today, DateOnly? dueSoonLimit, CancellationToken cancellationToken);

    /// <summary>Los tickets abiertos (no <c>Closed</c>) de la conversación, con tracking: los candidatos de un entrante (plan E5).
    /// Cada uno decide con <see cref="SupportTicket.CoversInbound"/> si la hora del mensaje cae en su ventana.</summary>
    Task<IReadOnlyList<SupportTicket>> ListOpenByConversationAsync(
        Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Los de la conversación y, si hay cliente, también los de ese cliente por otros canales, en cualquier estado y sin
    /// tracking: la pestaña de Soporte de la ficha de la conversación.</summary>
    Task<IReadOnlyList<SupportTicket>> ListByConversationOrCustomerAsync(
        Guid tenantId, Guid conversationId, Guid? customerId, CancellationToken cancellationToken);

    /// <summary>Fase 4a (plan G4): si algún ticket del tenant, en cualquier estado, usa el tipo. Es la pregunta previa del borrado; la
    /// regla la cierra la FK.</summary>
    Task<bool> AnyOfTypeAsync(Guid tenantId, SupportTicketTypeId typeId, CancellationToken cancellationToken);

    /// <summary>Fase 4a (plan G6): los tipos del tenant que algún ticket usa, para el <c>InUse</c> de la lista.</summary>
    Task<IReadOnlySet<SupportTicketTypeId>> UsedTypeIdsAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(SupportTicket ticket);
}

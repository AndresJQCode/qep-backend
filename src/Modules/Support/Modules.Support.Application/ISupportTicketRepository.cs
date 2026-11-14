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

    void Add(SupportTicket ticket);
}

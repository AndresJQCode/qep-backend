using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>La clave de consulta de los tickets (plan F7). Guardarla no toca la fila del ticket ni su versión.</summary>
public interface ISupportTicketAccessRepository
{
    /// <summary>Con tracking.</summary>
    Task<SupportTicketAccess?> FindAsync(Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken);

    /// <summary>El ticket del radicado (sin tracking: la consulta no lo modifica) y su clave (con tracking: el intento se anota). Null
    /// si el radicado no es del tenant o no tiene clave.</summary>
    Task<(SupportTicket Ticket, SupportTicketAccess Access)?> FindByNumberAsync(
        Guid tenantId, long number, CancellationToken cancellationToken);

    void Add(SupportTicketAccess access);
}

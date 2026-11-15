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

    /// <summary>
    /// Como <see cref="FindByNumberAsync"/>, pero la clave se lee con <c>SELECT … FOR UPDATE</c>: exige la transacción de
    /// <see cref="ISupportUnitOfWork.BeginTransactionAsync"/> (sin ella lanza). La consulta y el reenvío la usan para que mirar el bloqueo
    /// o el enfriamiento y anotar el intento sean una sola cosa: sin el candado, dos intentos a la vez leen el mismo contador y uno se
    /// pierde (revisión del Bloque B, Important 1). Se llama antes de cargar la clave de ese radicado en el mismo contexto: una ya
    /// cargada no se refresca.
    /// </summary>
    Task<(SupportTicket Ticket, SupportTicketAccess Access)?> LockByNumberAsync(
        Guid tenantId, long number, CancellationToken cancellationToken);

    void Add(SupportTicketAccess access);
}

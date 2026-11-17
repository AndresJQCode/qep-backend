using Modules.Support.Domain;

namespace Modules.Support.Application;

public interface ISupportTicketTypeRepository
{
    /// <summary>Todos los tipos del tenant por <c>Position</c>, con tracking.</summary>
    Task<IReadOnlyList<SupportTicketType>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<SupportTicketType?> FindAsync(Guid tenantId, SupportTicketTypeId id, CancellationToken cancellationToken);

    void Add(SupportTicketType type);

    /// <summary>Fase 4a (plan G4): el borrado se aplica al guardar. Si un ticket lo usa, la FK <c>RESTRICT</c> lo frena y el unit of
    /// work responde <c>support.ticket_type.in_use</c>.</summary>
    void Remove(SupportTicketType type);
}

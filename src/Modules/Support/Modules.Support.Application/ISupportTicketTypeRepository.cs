using Modules.Support.Domain;

namespace Modules.Support.Application;

public interface ISupportTicketTypeRepository
{
    /// <summary>Todos los tipos del tenant por <c>Position</c>, con tracking.</summary>
    Task<IReadOnlyList<SupportTicketType>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<SupportTicketType?> FindAsync(Guid tenantId, SupportTicketTypeId id, CancellationToken cancellationToken);

    void Add(SupportTicketType type);
}

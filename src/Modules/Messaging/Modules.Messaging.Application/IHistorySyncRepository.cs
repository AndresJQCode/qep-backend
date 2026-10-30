using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-29 §5: la importación del historial de una conexión, siempre por <c>(tenant_id, connection_id)</c>: la de otro
/// tenant responde igual que una que no existe. El agregado se guarda con <see cref="IMessagingUnitOfWork"/>.</summary>
public interface IHistorySyncRepository
{
    /// <summary>Con tracking. <c>null</c> si la conexión no tiene importación o es de otro tenant.</summary>
    Task<HistorySync?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

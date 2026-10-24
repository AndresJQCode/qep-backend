namespace Modules.Suppliers.Application;

// Auditoría operativa por el camino del outbox: acumula el evento en el DbContext del propio
// módulo para que se confirme en la misma transacción que el cambio.
public interface ISuppliersAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);
}

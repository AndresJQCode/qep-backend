namespace Modules.Catalog.Application;

// Auditoría operativa por el camino de outbox: acumula el evento en el DbContext del propio
// módulo para que commitee en la misma transacción que el cambio del catálogo. Administrar un
// producto es operativo, no crítico-de-seguridad-síncrono, la misma decisión que tomó Storage.
public interface ICatalogAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);
}

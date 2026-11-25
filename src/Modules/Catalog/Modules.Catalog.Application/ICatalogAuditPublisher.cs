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

    /// <summary>
    /// Spec 2026-11-22: la auditoría de un recurso que no es un producto (<c>price_list</c>, <c>channel_prices</c>), con los
    /// campos escritos. La implementación por defecto cae en <see cref="Publish"/> para que los dobles de prueba que ya existen
    /// sigan compilando; el publicador real la sobrescribe.
    /// </summary>
    void PublishResource(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        Publish(tenantId, actorId, action, resourceId, "success", occurredAt);
}

namespace Modules.Storage.Application;

// Auditoría operativa (ADR 0019, camino de outbox): acumula un evento de auditoría para
// commitear con la operación de archivo en la misma unidad de trabajo; el worker de
// proyección del módulo Audit lo escribe en audit.entries. Storage usa el camino de outbox
// (y no el IAuditRecorder atómico, que está ligado al DbContext de un productor) porque sus
// operaciones son operativas, no críticas-de-seguridad-síncronas.
public interface IStorageAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);

    // Spec 2026-09-16: lo que hace un proceso sin persona detrás, como los barridos de Storage.
    // actorType System y actorId vacío, el sentinela de sistema del repositorio
    // (QuotationExpirationProcessor). tenantId es null cuando lo afectado no es de un tenant, como
    // un objeto huérfano del bucket público.
    void PublishSystem(
        Guid? tenantId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);
}

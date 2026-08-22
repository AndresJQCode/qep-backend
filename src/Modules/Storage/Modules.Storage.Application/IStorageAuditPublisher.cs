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
}

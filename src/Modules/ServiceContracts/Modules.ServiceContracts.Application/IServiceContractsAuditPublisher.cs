namespace Modules.ServiceContracts.Application;

// Auditoría operativa como puerto propio del módulo (precedente ISuppliersAuditPublisher): el adaptador
// vive en Bootstrapper, sobre IAuditRecorder, para que Application no referencie Audit. Suma resourceType y
// changedFields respecto de Suppliers porque el spec (§4) los pide por acción.
public interface IServiceContractsAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

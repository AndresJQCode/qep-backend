namespace Modules.Companies.Application;

// Auditoria operativa por el camino de outbox: acumula el evento en el DbContext del propio
// modulo para que commitee en la misma transaccion que el cambio. Administrar una empresa es
// operativo, no critico-de-seguridad-sincrono, la misma decision que tomaron Storage y Catalog.
public interface ICompaniesAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);
}

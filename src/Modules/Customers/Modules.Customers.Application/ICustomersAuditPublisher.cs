namespace Modules.Customers.Application;

// Auditoria operativa por el camino de outbox: acumula el evento en el DbContext del propio
// modulo para que commitee en la misma transaccion que el cambio. Administrar clientes es
// operativo, no critico-de-seguridad-sincrono, la misma decision que tomaron Storage, Catalog y
// Companies.
public interface ICustomersAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);

    /// <summary>
    /// Spec de Shopify 2026-10-15 §6.2.1 (H7): lo que hace el sistema por una integración, sin persona detrás. Va con
    /// actor vacío y tipo <c>Integration</c>, para que la bitácora no lo confunda con un humano ni con un trabajo
    /// interno (<c>System</c>).
    /// </summary>
    void PublishByIntegration(
        Guid tenantId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);
}

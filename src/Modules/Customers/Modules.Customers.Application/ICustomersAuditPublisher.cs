namespace Modules.Customers.Application;

// Auditoria operativa por el camino de outbox: acumula el evento en el DbContext del propio
// modulo para que commitee en la misma transaccion que el cambio. Administrar clientes es
// operativo, no critico-de-seguridad-sincrono, la misma decision que tomaron Storage, Catalog y
// Companies.
public interface ICustomersAuditPublisher
{
    /// <param name="source">Quién audita cuando no es el propio módulo (la caja manda <c>"pos"</c>, como
    /// <c>PosAuditPublisher</c>); <c>null</c> deja <c>"customers"</c>.</param>
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt,
        string? source = null);

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

    /// <summary>
    /// Spec 2026-10-26 D-9: lo que registra el sistema sin persona detrás ni integración externa (la respuesta al bot de la
    /// política de datos). Va con actor vacío y tipo <c>System</c>.
    /// </summary>
    void PublishBySystem(
        Guid tenantId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);
}

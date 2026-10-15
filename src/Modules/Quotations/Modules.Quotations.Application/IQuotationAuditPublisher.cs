namespace Modules.Quotations.Application;

// Administrar una cotización es operativo, no crítico-de-seguridad-síncrono: mismo criterio que
// ICatalogAuditPublisher/IStorageAuditPublisher — va por outbox, no por el IAuditRecorder atómico.
public interface IQuotationAuditPublisher
{
    void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt);

    /// <summary>Lo que hace una integración sin persona detrás (spec 2026-10-15 §5.4): actor
    /// <c>Integration</c>, sin id de actor. Sin esto, un pedido de Shopify quedaría auditado como si
    /// lo hubiera hecho un humano con id vacío.</summary>
    void PublishByIntegration(
        Guid tenantId, string action, string resourceId, string outcome, DateTimeOffset occurredAt);
}

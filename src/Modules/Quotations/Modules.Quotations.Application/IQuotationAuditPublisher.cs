namespace Modules.Quotations.Application;

// Administrar una cotización es operativo, no crítico-de-seguridad-síncrono: mismo criterio que
// ICatalogAuditPublisher/IStorageAuditPublisher — va por outbox, no por el IAuditRecorder atómico.
public interface IQuotationAuditPublisher
{
    void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt);

    /// <summary>Como <see cref="Publish"/>, con detalle de ids (nunca datos personales) en <c>changedFields</c>, el único
    /// campo libre del evento de auditoría: la proyección lo guarda tal cual en <c>audit.entries</c>. Lo usa el cambio de la
    /// cuenta de comprador del pedido (spec 2026-11-15 §4), que audita el id anterior y el nuevo.</summary>
    void PublishWithDetails(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt, IReadOnlyDictionary<string, string?> details);

    /// <summary>Lo que hace una integración sin persona detrás (spec 2026-10-15 §5.4): actor
    /// <c>Integration</c>, sin id de actor. Sin esto, un pedido de Shopify quedaría auditado como si
    /// lo hubiera hecho un humano con id vacío.</summary>
    void PublishByIntegration(
        Guid tenantId, string action, string resourceId, string outcome, DateTimeOffset occurredAt);
}

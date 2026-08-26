namespace Modules.Quotations.Application;

// Administrar una cotización es operativo, no crítico-de-seguridad-síncrono: mismo criterio que
// ICatalogAuditPublisher/IStorageAuditPublisher — va por outbox, no por el IAuditRecorder atómico.
public interface IQuotationAuditPublisher
{
    void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt);
}

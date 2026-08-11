namespace Modules.Catalog.Application;

// Operational audit over the outbox path: buffers the event into the module own DbContext
// so it commits in the same transaction as the catalogue change. Administering a product is
// operational, not security-critical-synchronous, which is the same call Storage made.
public interface ICatalogAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt);
}

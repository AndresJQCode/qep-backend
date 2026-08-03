namespace Modules.Storage.Application;

// Operational audit (ADR 0019 outbox path): buffers an audit event to be committed with
// the file operation in the same unit of work; the Audit module's projection worker writes
// it to audit.entries. Storage uses the outbox path (not the atomic IAuditRecorder, which is
// bound to a producer's own DbContext) because its operations are operational, not
// security-critical-synchronous.
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

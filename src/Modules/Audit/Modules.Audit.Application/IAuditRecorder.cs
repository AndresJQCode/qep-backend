using Modules.Audit.Domain;

namespace Modules.Audit.Application;

/// <summary>
/// The transversal audit contract (capability contract: <c>Audit | todos |
/// IAuditRecorder + audit outbox</c>). Any module's application services record audited
/// actions through it. The atomic implementation (ADR 0019) buffers the entry in the
/// caller's own unit of work, so the audit row commits or rolls back together with the
/// business change — an audit failure blocks a security action. Operational, eventual
/// audit is produced through the audit outbox and projected by the Audit module.
/// </summary>
public interface IAuditRecorder
{
    void Record(
        Guid? tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human,
        string source = "");
}

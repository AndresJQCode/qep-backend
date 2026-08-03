using Modules.Audit.Domain;
using Modules.Identity.Application;

namespace Modules.Identity.Infrastructure.Persistence;

// Atomic audit path (ADR 0019) for Identity: buffers the audit entry in
// IdentityDbContext so it commits or rolls back together with the session
// issue/revoke in the same unit of work. audit.entries is owned by the Audit
// module's migrations; IdentityDbContext maps it as an ExcludeFromMigrations write
// projection, same as TenancyDbContext does for Tenancy.
internal sealed class IdentityAuditRecorder(IdentityDbContext dbContext) : IIdentityAuditRecorder
{
    public void Record(
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human)
    {
        var entry = AuditEntry.Create(
            tenantId: null,
            actorId,
            actorType,
            action,
            resourceType,
            resourceId,
            outcome,
            changedFieldsJson: "[]",
            source: "identity",
            occurredAt);
        dbContext.AuditEntries.Add(entry);
    }
}

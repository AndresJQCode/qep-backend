using System.Text.Json;
using Modules.Audit.Application;
using Modules.Audit.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

// Atomic audit path (ADR 0019) for Tenancy: buffers the audit entry in TenancyDbContext
// so it commits or rolls back together with the business change in the same unit of work.
// audit.entries is owned by the Audit module's migrations; TenancyDbContext maps it as an
// ExcludeFromMigrations write projection.
internal sealed class TenancyAuditRecorder(TenancyDbContext dbContext) : IAuditRecorder
{
    public void Record(
        Guid? tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human,
        string source = "")
    {
        var entry = AuditEntry.Create(
            tenantId,
            actorId,
            actorType,
            action,
            resourceType,
            resourceId,
            outcome,
            JsonSerializer.Serialize(changedFields),
            string.IsNullOrWhiteSpace(source) ? DeriveSource(action) : source,
            occurredAt);
        dbContext.AuditEntries.Add(entry);
    }

    // Default source is the module prefix of the action code (`<module>.<resource>.<verb>`).
    private static string DeriveSource(string action)
    {
        var separator = action.IndexOf('.');
        return separator > 0 ? action[..separator] : action;
    }
}

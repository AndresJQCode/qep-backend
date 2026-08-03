using Modules.Audit.Domain;

namespace Modules.Identity.Application;

/// <summary>
/// Identity's own atomic audit path (ADR 0019), buffered in <c>IdentityDbContext</c>'s
/// unit of work. Deliberately not the shared cross-module <c>IAuditRecorder</c>: that
/// interface is bound once per DbContext (Tenancy already binds it to
/// <c>TenancyDbContext</c>), and a second global binding to <c>IdentityDbContext</c>
/// would shadow it in the DI container — whichever module registers last would
/// silently steal every other module's audit writes into the wrong DbContext, and
/// those entries would then never be saved by the writing module's own unit of work.
/// </summary>
public interface IIdentityAuditRecorder
{
    void Record(
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human);
}

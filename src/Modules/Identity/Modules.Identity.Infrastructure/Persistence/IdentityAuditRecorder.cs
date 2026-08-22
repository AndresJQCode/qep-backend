using Modules.Audit.Domain;
using Modules.Identity.Application;

namespace Modules.Identity.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019) para Identity: acumula la entrada de auditoría en
// IdentityDbContext para que commitee o revierta junto con la emisión/revocación de sesión
// en la misma unidad de trabajo. audit.entries es propiedad de las migraciones del módulo
// Audit; IdentityDbContext la mapea como proyección de escritura ExcludeFromMigrations,
// igual que hace TenancyDbContext para Tenancy.
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

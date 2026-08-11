using System.Text.Json;
using Modules.Audit.Application;
using Modules.Audit.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019) para Tenancy: acumula la entrada de auditoría en
// TenancyDbContext para que commitee o revierta junto con el cambio de negocio, en la misma
// unidad de trabajo. audit.entries es propiedad de las migraciones del módulo Audit;
// TenancyDbContext la mapea como proyección de escritura ExcludeFromMigrations.
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

    // La fuente por defecto es el prefijo de módulo del código de acción (`<module>.<resource>.<verb>`).
    private static string DeriveSource(string action)
    {
        var separator = action.IndexOf('.');
        return separator > 0 ? action[..separator] : action;
    }
}

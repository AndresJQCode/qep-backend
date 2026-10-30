using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Billing.Application;

namespace Modules.Billing.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia de PipelineAuditRecorder: la entrada se acumula en BillingDbContext y
// commitea o revierte con el cambio de negocio, en la misma unidad de trabajo. No pasa por IAuditRecorder: la
// implementación registrada de ese contrato acumula en TenancyDbContext, que BillingUnitOfWork no guarda, así que la fila
// se perdería sin error. El adaptador de Bootstrapper del ruling F1 no puede cumplir «misma transacción» por esa razón.
internal sealed class BillingAuditPublisher(BillingDbContext dbContext) : IBillingAuditPublisher
{
    private const string Source = "billing";

    public void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt) =>
        dbContext.AuditEntries.Add(AuditEntry.Create(
            tenantId,
            actorId,
            AuditActorType.Human,
            action,
            resourceType,
            resourceId,
            outcome,
            JsonSerializer.Serialize(changedFields),
            Source,
            occurredAt));
}

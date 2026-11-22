using System.Text.Json;
using Modules.Audit.Domain;
using Modules.Payroll.Application;

namespace Modules.Payroll.Infrastructure.Persistence;

// Camino de auditoría atómica (ADR 0019), copia del de Hr: la entrada se acumula en PayrollDbContext y commitea o revierte con el
// cambio de negocio, en la misma unidad de trabajo. No pasa por IAuditRecorder, cuya implementación acumula en TenancyDbContext,
// que PayrollUnitOfWork no guarda: la fila se perdería sin error.
internal sealed class PayrollAuditPublisher(PayrollDbContext dbContext) : IPayrollAuditPublisher
{
    private const string Source = "payroll";

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

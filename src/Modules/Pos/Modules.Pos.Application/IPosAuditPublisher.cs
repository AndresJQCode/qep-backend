namespace Modules.Pos.Application;

/// <summary>
/// platform.audit.recorded.v1 en la proyección de outbox, en el mismo SaveChanges que el cambio.
/// changedFields es el único campo libre del contrato: ahí viajan los descuentos de la venta
/// (spec, «Descuentos en la auditoría»).
/// </summary>
public interface IPosAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

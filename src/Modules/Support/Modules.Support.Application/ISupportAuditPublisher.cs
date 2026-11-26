namespace Modules.Support.Application;

// Auditoría operativa como puerto propio del módulo (mismo contrato que ILoansAuditPublisher): el adaptador acumula la entrada en
// el contexto del módulo para que commitee o revierta con el cambio de negocio.
public interface ISupportAuditPublisher
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

    /// <summary>Una acción del sistema, sin persona detrás: el seguimiento del hilo que reabre un ticket (plan E6). Sale con el actor
    /// vacío y de tipo sistema, como las de los workers de Identity.</summary>
    void PublishSystem(
        Guid tenantId,
        string action,
        string resourceType,
        string resourceId,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

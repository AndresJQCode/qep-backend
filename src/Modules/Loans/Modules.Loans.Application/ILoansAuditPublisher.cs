namespace Modules.Loans.Application;

// Auditoría operativa como puerto propio del módulo (mismo contrato que IServiceContractsAuditPublisher): el adaptador
// acumula la entrada en el contexto del módulo para que commitee o revierta con el cambio de negocio.
public interface ILoansAuditPublisher
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

namespace Modules.Hr.Application;

// Auditoría operativa como puerto propio del módulo (mismo contrato que ILoansAuditPublisher): el adaptador acumula la entrada en
// el contexto del módulo para que commitee o revierta con el cambio de negocio. `audit.entries` guarda nombres de campos, nunca
// valores: el antes y el después viven en hr.employee_activity.
public interface IHrAuditPublisher
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

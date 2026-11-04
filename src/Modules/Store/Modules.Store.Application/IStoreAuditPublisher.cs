namespace Modules.Store.Application;

// Auditoría operativa como puerto propio del módulo (mismo contrato que ILoansAuditPublisher): el adaptador acumula la entrada en
// el contexto del módulo para que commitee o revierta con el cambio de negocio.
public interface IStoreAuditPublisher
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

    /// <summary>
    /// Lo que hace el visitante de la tienda pública: no hay sesión ni actor, así que se audita como integración.
    /// </summary>
    void PublishAnonymous(Guid tenantId, string action, string resourceType, string resourceId, DateTimeOffset at);
}

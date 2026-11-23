using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

// Auditoría operativa como puerto propio del módulo (mismo contrato que IHrAuditPublisher): el adaptador acumula la entrada en el
// contexto del módulo para que commitee o revierta con el cambio de negocio. No publica eventos de outbox (plan, corrección 4).
// `audit.entries` guarda nombres de campos, nunca valores.
public interface ISchedulingAuditPublisher
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
    /// La misma entrada, escrita a nombre del agente de IA (<c>AuditActorType.AiAgent</c>, con el id del agente como actor): lo que dice que fue
    /// el agente y no una persona. Sin miembro: el actor no es un usuario del tenant (spec 2026-11-23, D-8).
    /// </summary>
    void PublishByAgent(
        Guid tenantId,
        Guid agentId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

internal static class SchedulingAudit
{
    /// <summary>Acumula la entrada en el contexto del módulo; la confirma el <c>SaveChangesAsync</c> del caso de uso, junto con el cambio.</summary>
    public static void Publish(
        ISchedulingAuditPublisher audit,
        IExecutionContext executionContext,
        Guid tenantId,
        string action,
        string resourceType,
        Guid resourceId,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset now) =>
        audit.Publish(tenantId, executionContext.SubjectId, action, resourceType, resourceId.ToString(), "success", changedFields, now);

    /// <summary>Lo mismo con quien escribe ya resuelto: el agente se audita a su nombre y una persona, con su sujeto.</summary>
    public static void Publish(
        ISchedulingAuditPublisher audit,
        SchedulingActor actor,
        Guid tenantId,
        string action,
        string resourceType,
        Guid resourceId,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset now)
    {
        if (actor.IsAgent)
        {
            audit.PublishByAgent(tenantId, actor.AuditActorId, action, resourceType, resourceId.ToString(), "success", changedFields, now);
            return;
        }

        audit.Publish(tenantId, actor.AuditActorId, action, resourceType, resourceId.ToString(), "success", changedFields, now);
    }
}

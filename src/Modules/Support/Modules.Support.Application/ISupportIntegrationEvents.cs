using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Eventos que Soporte le publica a otros módulos por el outbox de la plataforma (spec 2026-11-22 §12). El adaptador los acumula en el
/// contexto de Soporte, así que commitean o revierten con el cambio que los causa, en la misma unidad de trabajo; nada de esto llama a
/// <c>Notifications</c> directo (ArchitectureTests lo prohíbe entre módulos de negocio). El nombre y el payload de cada evento son un
/// contrato con quien los lea: nunca se cambian sin versionarlos.
/// </summary>
public interface ISupportIntegrationEvents
{
    /// <summary>
    /// <c>support.ticket.customer-replied.v1</c>: el cliente contestó y el ticket que esperaba volvió a <see cref="SupportTicketStatus.Open"/>.
    /// El destinatario es el responsable del ticket; quien llama ya decidió que hay uno y que no es quien causó el cambio.
    /// </summary>
    void PublishCustomerReplied(SupportTicket ticket, DateTimeOffset occurredAt);

    /// <summary>
    /// <c>support.ticket.unassigned-alert.v1</c>: el ticket lleva <paramref name="thresholdHours"/> horas hábiles sin responsable. Lo
    /// produce el worker del aviso (Tarea 12 del plan); <paramref name="unassignedSince"/> identifica el tramo, y con él el consumidor
    /// desduplica un reintento.
    /// </summary>
    void PublishUnassignedAlert(
        SupportTicket ticket,
        IReadOnlyList<Guid> recipientMemberIds,
        int thresholdHours,
        DateTimeOffset unassignedSince,
        DateTimeOffset occurredAt);

    /// <summary>
    /// <c>support.ticket.task-assigned.v1</c> (spec de tareas internas 2026-11-22 §6 D-3): una tarea pasó a
    /// <paramref name="ticket"/>.AssigneeMemberId, asignada por <paramref name="assignedByMemberId"/>. Payload camelCase: <c>ticketId</c>,
    /// <c>tenantId</c>, <c>ticketNumber</c>, <c>assigneeMemberId</c>, <c>assignedByMemberId</c>. El llamante decide si aplica (nunca
    /// cuando se la asigna a sí mismo).
    /// </summary>
    void PublishTaskAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt);

    /// <summary>
    /// <c>support.ticket.assigned.v1</c> (task-notices spec 2026-11-22 §5.2, D-7): a <b>customer</b> ticket passed to
    /// <paramref name="ticket"/>.AssigneeMemberId, assigned by <paramref name="assignedByMemberId"/>. The payload is the same as
    /// <c>task-assigned</c> (<c>ticketId</c>, <c>tenantId</c>, <c>ticketNumber</c>, <c>assigneeMemberId</c>, <c>assignedByMemberId</c>);
    /// tasks keep their own event. The caller decides if it applies (never when assigning to oneself or leaving it unassigned).
    /// </summary>
    void PublishTicketAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt);

    /// <summary>
    /// <c>support.ticket.assignee-released.v1</c> (spec 2026-11-22 §5.2, D-7): <paramref name="formerAssigneeMemberId"/> stopped being the
    /// assignee, for tasks and customer tickets alike. <paramref name="reason"/> is one of <see cref="SupportAssigneeReleaseReasons"/>.
    /// Payload camelCase: <c>ticketId</c>, <c>tenantId</c>, <c>ticketNumber</c>, <c>formerAssigneeMemberId</c>, <c>reason</c>,
    /// <c>isInternal</c>.
    /// </summary>
    void PublishAssigneeReleased(SupportTicket ticket, Guid formerAssigneeMemberId, string reason, DateTimeOffset occurredAt);
}

/// <summary>The <c>reason</c> of <c>support.ticket.assignee-released.v1</c>: a contract with its consumers, never renamed.</summary>
public static class SupportAssigneeReleaseReasons
{
    public const string Reassigned = "Reassigned";
    public const string Resolved = "Resolved";
    public const string Closed = "Closed";
}

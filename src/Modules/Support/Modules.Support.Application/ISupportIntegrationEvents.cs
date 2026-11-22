using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Los eventos de integración de Soporte (spec 2026-11-22 §6 D-3). El nombre y el payload son un contrato con quien los lea
/// (el spec hermano de avisos): nunca se cambian sin versionarlos.</summary>
public static class SupportIntegrationEventNames
{
    /// <summary>Una tarea quedó a cargo de alguien que no es quien la asignó. Payload camelCase: <c>ticketId</c>, <c>tenantId</c>,
    /// <c>ticketNumber</c>, <c>assigneeMemberId</c>, <c>assignedByMemberId</c>.</summary>
    public const string TaskAssigned = "support.ticket.task-assigned.v1";
}

/// <summary>
/// Escribe los eventos de integración de Soporte en el outbox de plataforma, en la misma transacción del handler: el evento se confirma
/// o se revierte con el cambio que lo causó. Sólo acumula en el contexto del módulo; quien llama lo guarda con su
/// <c>SaveChanges</c>. Hasta que exista un consumidor, el evento queda escrito y el procesador lo marca sin efecto (plan D14).
/// </summary>
public interface ISupportIntegrationEvents
{
    /// <summary>Una tarea pasó a <paramref name="ticket"/>.AssigneeMemberId, asignada por <paramref name="assignedByMemberId"/>. El
    /// llamante decide si aplica (nunca cuando se la asigna a sí mismo).</summary>
    void PublishTaskAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt);
}

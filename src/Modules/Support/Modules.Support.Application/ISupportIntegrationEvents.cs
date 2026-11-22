using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Eventos que Soporte le publica a otros módulos por el outbox de la plataforma (spec 2026-11-22 §12). El adaptador los acumula en el
/// contexto de Soporte, así que commitean o revierten con el cambio que los causa, en la misma unidad de trabajo; nada de esto llama a
/// <c>Notifications</c> directo (ArchitectureTests lo prohíbe entre módulos de negocio).
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
}

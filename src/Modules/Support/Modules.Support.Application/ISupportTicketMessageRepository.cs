using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Los vínculos de mensajes del hilo con un ticket (spec §8.2). Un mensaje repetido en el mismo ticket choca con
/// <c>IX_ticket_messages_ticket_message</c> al guardar: <c>support.message.already_linked</c>.</summary>
public interface ISupportTicketMessageRepository
{
    /// <summary>Con tracking.</summary>
    Task<SupportTicketMessage?> FindAsync(Guid tenantId, SupportTicketId ticketId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>Los vinculados y fijados (nunca los soltados), del más nuevo al más viejo por <c>(occurred_at, message_id)</c>.
    /// <paramref name="before"/> es el último de la página anterior: trae los estrictamente anteriores.</summary>
    Task<IReadOnlyList<SupportTicketMessage>> ListVisibleAsync(
        Guid tenantId,
        SupportTicketId ticketId,
        (DateTimeOffset OccurredAt, Guid MessageId)? before,
        int take,
        CancellationToken cancellationToken);

    void Add(SupportTicketMessage link);
}

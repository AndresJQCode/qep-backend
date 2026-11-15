using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Las respuestas del ticket (plan E16). El mismo <c>clientId</c> dos veces en un ticket choca con
/// <c>IX_ticket_replies_ticket_client</c> al guardar: <c>support.reply.duplicate_client_id</c>.</summary>
public interface ISupportReplyRepository
{
    /// <summary>Con tracking: el envío confirma la fila Pending y después la marca.</summary>
    Task<SupportTicketReply?> FindByClientIdAsync(
        Guid tenantId, SupportTicketId ticketId, Guid clientId, CancellationToken cancellationToken);

    /// <summary>Por <c>CreatedAt</c>, de la más vieja a la más nueva.</summary>
    Task<IReadOnlyList<SupportTicketReply>> ListAsync(Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken);

    void Add(SupportTicketReply reply);
}

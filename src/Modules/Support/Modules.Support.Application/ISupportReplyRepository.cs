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

    /// <summary>Plan E17: una respuesta que el canal rechazó antes de enviar (la ventana se cerró, el contacto se bloqueó entre la
    /// revisión y el envío) no deja fila; la <c>Pending</c> ya confirmada se borra. Su documento formal, si lo tiene, se va con ella
    /// por la cascada de la FK (fase 4b, H9): para el solicitante esa respuesta nunca existió.</summary>
    void Remove(SupportTicketReply reply);

    /// <summary>Fase 4b (H9): el documento de la respuesta formal, en el mismo <c>SaveChanges</c> que su respuesta. Insert-only.</summary>
    void AddDocument(SupportReplyDocument document);

    /// <summary>Sin tracking: el documento no cambia nunca.</summary>
    Task<SupportReplyDocument?> FindDocumentAsync(
        Guid tenantId, SupportTicketId ticketId, Guid replyId, CancellationToken cancellationToken);

    /// <summary>Los documentos de las respuestas del ticket, por respuesta, para armar el detalle y la consulta pública.</summary>
    Task<IReadOnlyList<SupportReplyDocument>> ListDocumentsAsync(
        Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken);
}

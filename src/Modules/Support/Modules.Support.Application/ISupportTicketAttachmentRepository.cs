using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Los adjuntos de un ticket (plan F35). Se agregan en la transacción del radicado y no se editan.</summary>
public interface ISupportTicketAttachmentRepository
{
    void Add(SupportTicketAttachment attachment);

    /// <summary>Sin tracking, en el orden en que se adjuntaron.</summary>
    Task<IReadOnlyList<SupportTicketAttachment>> ListAsync(
        Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken);

    /// <summary>Sin tracking. Null si el adjunto no es de ese ticket del tenant.</summary>
    Task<SupportTicketAttachment?> FindAsync(
        Guid tenantId, SupportTicketId ticketId, Guid attachmentId, CancellationToken cancellationToken);
}

using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>Un comando y no una consulta, como <c>IssueDownloadUrl</c> de Storage: emitir la URL deja auditoría.</summary>
public sealed record GetTicketAttachmentDownloadCommand(Guid TenantId, Guid TicketId, Guid AttachmentId)
    : ICommand<SupportAttachmentDownloadDto>;

/// <summary>
/// Bajar un adjunto del formulario público desde la consola (plan F37). Lo autoriza <c>support.ticket.view</c> (leer o gestionar),
/// revalidado acá: otro tenant es 403, nunca 404. El asesor de Soporte no necesariamente tiene <c>storage.file.read</c>, por eso la URL
/// la firma el puerto de Soporte y no el endpoint de Storage. La auditoría es de Soporte, a nombre de quien bajó.
/// </summary>
public sealed class GetTicketAttachmentDownloadHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportTicketAttachmentRepository attachments,
    ISupportAttachmentStorage storage,
    ISupportAuditPublisher auditPublisher,
    ISupportUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<GetTicketAttachmentDownloadCommand, SupportAttachmentDownloadDto>
{
    private static readonly string[] NoFields = [];

    public async Task<SupportAttachmentDownloadDto> HandleAsync(
        GetTicketAttachmentDownloadCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);

        var ticketId = new SupportTicketId(command.TicketId);
        _ = await scope.ViewAsync(command.TenantId, command.TicketId, cancellationToken);
        var attachment = await attachments.FindAsync(command.TenantId, ticketId, command.AttachmentId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                SupportErrorCodes.AttachmentNotFound, $"Attachment '{command.AttachmentId}' was not found in this ticket.");

        var url = await storage.CreateDownloadUrlAsync(command.TenantId, attachment.FileId, cancellationToken);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.TicketAttachmentDownloaded,
            SupportAuditActions.Resources.Ticket,
            ticketId.ToString(),
            "success",
            NoFields,
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SupportAttachmentDownloadDto(url.AbsoluteUri);
    }
}

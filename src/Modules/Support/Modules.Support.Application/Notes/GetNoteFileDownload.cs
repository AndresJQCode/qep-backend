using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>A command and not a query, like <see cref="GetTicketAttachmentDownloadCommand"/>: issuing the URL leaves an audit entry.</summary>
public sealed record GetNoteFileDownloadCommand(Guid TenantId, Guid TicketId, Guid NoteId, Guid FileId)
    : ICommand<SupportAttachmentDownloadDto>;

/// <summary>
/// Downloading a note's file (spec 2026-11-15 §5.4, plan N21): <c>support.ticket.view</c> (read or manage), revalidated here, so
/// another tenant is 403. A note that is not of this ticket, or a file that is not of this note, is 404
/// <c>support.attachment.not_found</c> (owner O1: an existing code). The URL is signed for 5 minutes by Support's port, and the audit
/// is <c>support.ticket.attachment_downloaded</c> on the ticket, like a public form attachment.
/// </summary>
public sealed class GetNoteFileDownloadHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportTicketNoteRepository notes,
    ISupportAttachmentStorage storage,
    ISupportAuditPublisher auditPublisher,
    ISupportUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<GetNoteFileDownloadCommand, SupportAttachmentDownloadDto>
{
    private static readonly string[] NoFields = [];

    public async Task<SupportAttachmentDownloadDto> HandleAsync(GetNoteFileDownloadCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);

        var ticketId = new SupportTicketId(command.TicketId);
        _ = await scope.ViewAsync(command.TenantId, command.TicketId, cancellationToken);
        var note = await notes.FindAsync(command.TenantId, command.NoteId, cancellationToken);
        if (note is null || note.TicketId != ticketId || note.Files.All(file => file.FileId != command.FileId))
        {
            throw new ResourceNotFoundException(
                SupportErrorCodes.AttachmentNotFound, $"File '{command.FileId}' was not found in this ticket's note.");
        }

        var url = await storage.CreateDownloadUrlAsync(command.TenantId, command.FileId, cancellationToken);
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

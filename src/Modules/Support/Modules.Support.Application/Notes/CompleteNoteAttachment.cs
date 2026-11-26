using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record CompleteNoteAttachmentCommand(Guid TenantId, Guid TicketId, Guid FileId) : ICommand<SupportNoteAttachmentStatusDto>;

/// <summary>
/// Completing a note upload (plan N19): Storage runs the same pipeline as everywhere (size, magic bytes, antivirus, promotion) with the
/// owner of (ticket, caller). Another member's or another ticket's upload answers like one that does not exist: 422
/// <c>support.note.file_invalid</c>. Completing before the browser finished the <c>PUT</c> is 428
/// <c>support.public.attachment_not_uploaded</c> (owner O1: an existing code).
/// </summary>
public sealed class CompleteNoteAttachmentHandler(
    SupportTicketScope scope,
    ISupportAttachmentStorage storage)
    : ICommandHandler<CompleteNoteAttachmentCommand, SupportNoteAttachmentStatusDto>
{
    public async Task<SupportNoteAttachmentStatusDto> HandleAsync(
        CompleteNoteAttachmentCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, expectedVersion: null, cancellationToken);
        SupportNoteAttachmentRules.EnsureOpen(ticket);
        return await storage.CompleteAsync(
                command.TenantId, command.FileId, SupportNoteUploads.OwnerKey(ticket.Id, actor), cancellationToken) switch
        {
            SupportAttachmentCompletion.Available => new SupportNoteAttachmentStatusDto("available"),
            SupportAttachmentCompletion.Rejected => new SupportNoteAttachmentStatusDto("rejected"),
            SupportAttachmentCompletion.NotUploaded => throw new PreconditionRequiredException(
                SupportErrorCodes.PublicAttachmentNotUploaded, "The file has not been uploaded yet."),
            _ => throw SupportNoteAttachmentRules.FileInvalid(),
        };
    }
}

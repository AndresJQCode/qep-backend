using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>The body of <c>POST /tickets/{id}/notes/attachments</c>: what the browser is about to upload.</summary>
public sealed record SupportNoteAttachmentRequest(string? Name, string? MimeType, long? SizeBytes);

/// <summary>The upload session. The headers are signed and the browser must send them as they are in the <c>PUT</c>. There is no
/// upload key: the owner is derived on the server (<see cref="SupportNoteUploads.OwnerKey"/>).</summary>
public sealed record SupportNoteAttachmentSessionDto(Guid FileId, string UploadUrl, IReadOnlyDictionary<string, string> Headers);

/// <summary><c>available</c> or <c>rejected</c>: a rejected file is not an error, the screen says so before saving the note.</summary>
public sealed record SupportNoteAttachmentStatusDto(string Status);

public sealed record OpenNoteAttachmentCommand(Guid TenantId, Guid TicketId, string? Name, string? MimeType, long? SizeBytes)
    : ICommand<SupportNoteAttachmentSessionDto>
{
    public static OpenNoteAttachmentCommand From(Guid tenantId, Guid ticketId, SupportNoteAttachmentRequest request) =>
        new(tenantId, ticketId, request.Name, request.MimeType, request.SizeBytes);
}

/// <summary>Plan N19: the public form's limits (<see cref="SupportPublicAttachmentRules"/>: 10 MB, PDF/JPEG/PNG/WebP, a plain name of up
/// to 260), each on its field, the same rules as <see cref="OpenPublicAttachmentValidator"/>. Without the <c>attachments_busy</c> cap:
/// that one protects an anonymous endpoint.</summary>
public sealed class OpenNoteAttachmentValidator : AbstractValidator<OpenNoteAttachmentCommand>
{
    public OpenNoteAttachmentValidator()
    {
        RuleFor(command => command.Name)
            .Must(SupportPublicAttachmentRules.IsValidName)
            .WithMessage($"The file name must be a plain name of 1 to {SupportPublicAttachmentRules.NameMaxLength} characters.");
        RuleFor(command => command.Name)
            .Must(name => SupportPublicAttachmentRules.AllowedMimeTypesByExtension.ContainsKey(Path.GetExtension(name!.Trim())))
            .When(command => SupportPublicAttachmentRules.IsValidName(command.Name))
            .WithMessage("Only PDF, JPG, JPEG, PNG and WEBP files are allowed.");
        RuleFor(command => command.MimeType)
            .Must((command, mimeType) => SupportPublicAttachmentRules.IsAllowedType(command.Name, mimeType))
            .When(command => SupportPublicAttachmentRules.IsValidName(command.Name)
                && SupportPublicAttachmentRules.AllowedMimeTypesByExtension.ContainsKey(Path.GetExtension(command.Name!.Trim())))
            .WithMessage("The media type does not match the file extension.");
        RuleFor(command => command.SizeBytes)
            .Must(size => size is { } value && SupportPublicAttachmentRules.IsValidSize(value))
            .WithMessage($"The file must be between 1 byte and {SupportPublicAttachmentRules.MaxSizeBytes} bytes.");
    }
}

/// <summary>
/// Opening an upload for a note (spec 2026-11-15 §5.4, plan N19). Order: access (<c>support.ticket.manage</c> or <c>support.task.use</c>) →
/// validator → the ticket scope (active membership 403, ticket 404) → closed ticket (422 <c>support.note.ticket_closed</c>) → Storage's member-less upload
/// with the owner of (ticket, caller). A declaration Storage refuses after the validator passed is 422 <c>support.note.file_invalid</c>.
/// </summary>
public sealed class OpenNoteAttachmentHandler(
    SupportTicketScope scope,
    ISupportAttachmentStorage storage,
    IValidator<OpenNoteAttachmentCommand> validator)
    : ICommandHandler<OpenNoteAttachmentCommand, SupportNoteAttachmentSessionDto>
{
    public async Task<SupportNoteAttachmentSessionDto> HandleAsync(OpenNoteAttachmentCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, expectedVersion: null, cancellationToken);
        SupportNoteAttachmentRules.EnsureOpen(ticket);
        try
        {
            var upload = await storage.OpenAsync(
                command.TenantId,
                SupportNoteUploads.OwnerKey(ticket.Id, actor),
                command.Name!.Trim(),
                SupportPublicAttachmentRules.NormalizeMimeType(command.MimeType!),
                command.SizeBytes!.Value,
                cancellationToken);
            return new SupportNoteAttachmentSessionDto(upload.FileId, upload.UploadUrl.AbsoluteUri, upload.Headers);
        }
        catch (SupportDomainException)
        {
            // The adapter speaks the public form's code; a note answers with its own.
            throw SupportNoteAttachmentRules.FileInvalid();
        }
    }
}

internal static class SupportNoteAttachmentRules
{
    public static SupportDomainException FileInvalid() =>
        new(SupportErrorCodes.NoteFileInvalid, "The note file is not valid.");

    /// <summary>Plan N19: a closed ticket takes no new uploads (422 <c>support.note.ticket_closed</c>). The rest of the order every note
    /// write shares (access, validator, active membership, ticket, participant) is <see cref="SupportTicketScope.ActAsync"/>'s.</summary>
    public static void EnsureOpen(SupportTicket ticket)
    {
        if (ticket.Status is SupportTicketStatus.Closed)
        {
            throw AddTicketNoteHandler.Closed();
        }
    }
}

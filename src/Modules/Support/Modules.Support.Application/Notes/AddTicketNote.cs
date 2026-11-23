using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>The body of <c>POST /tickets/{id}/notes</c>, without <c>If-Match</c>. <c>ClientId</c> comes from the screen and becomes
/// the note's id: a retry repeats it and gets the same note. <c>FileIds</c> are uploads this member opened for this ticket, in the
/// order the note shows them.</summary>
public sealed record AddTicketNoteRequest(Guid? ClientId, string? Text, IReadOnlyList<Guid>? FileIds);

public sealed record AddTicketNoteCommand(Guid TenantId, Guid TicketId, Guid? ClientId, string? Text, IReadOnlyList<Guid>? FileIds)
    : ICommand<SupportTicketNoteDto>
{
    public static AddTicketNoteCommand From(Guid tenantId, Guid ticketId, AddTicketNoteRequest request) =>
        new(tenantId, ticketId, request.ClientId, request.Text, request.FileIds);
}

/// <summary>Plan N16: free text carries a validator (the field) besides the domain guard (the code), like
/// <see cref="SendTicketReplyValidator"/>. The files are judged by the handler: their codes depend on Storage and on the ticket.</summary>
public sealed class AddTicketNoteValidator : AbstractValidator<AddTicketNoteCommand>
{
    public AddTicketNoteValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty);
        RuleFor(command => command.Text)
            .Must(SupportTicketReply.IsValidText)
            .WithMessage($"Escribe una nota de 1 a {SupportTicketReply.TextMaxLength} caracteres, sin caracteres de control.");
    }
}

/// <summary>
/// Spec 2026-11-15 §5.4: an internal note. Order: access (<c>support.ticket.manage</c> or <c>support.task.use</c>) → validator → the ticket scope (active
/// membership 403, ticket 404 for what the member cannot see, 403 for what she can only read) → a note with this <c>clientId</c> (plan N15: same ticket and author answers it, even on a closed ticket; anything else
/// is 409 <c>concurrency.conflict</c>) → closed ticket (422 <c>support.note.ticket_closed</c>) → files → note and audit → commit →
/// notice on the hub → 201.
///
/// The note does not go through the <see cref="SupportTicket"/> aggregate: no version bump, no <c>ticket_events</c> row, no
/// integration event, and nothing of it reaches <c>public/support</c>.
/// </summary>
public sealed class AddTicketNoteHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportTicketNoteRepository notes,
    ISupportMemberDirectory members,
    ISupportViewer viewer,
    ISupportAttachmentStorage storage,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    IClock clock,
    IValidator<AddTicketNoteCommand> validator)
    : ICommandHandler<AddTicketNoteCommand, SupportTicketNoteDto>
{
    private static readonly string[] NoteFields = ["notes"];

    public async Task<SupportTicketNoteDto> HandleAsync(AddTicketNoteCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var (ticket, actor) = await scope.ActAsync(command.TenantId, command.TicketId, expectedVersion: null, cancellationToken);

        // Before the closed check: the retry of a note whose HTTP answer was lost gets its note even if the ticket closed since.
        var noteId = command.ClientId!.Value;
        var existing = await notes.FindAsync(command.TenantId, noteId, cancellationToken);
        if (existing is not null)
        {
            if (existing.TicketId != ticket.Id || existing.AuthorMemberId != actor)
            {
                throw ClientIdTaken();
            }

            return await SupportNoteDtos.ComposeAsync(members, viewer, command.TenantId, existing, cancellationToken);
        }

        if (ticket.Status is SupportTicketStatus.Closed)
        {
            throw Closed();
        }

        var files = await ResolveFilesAsync(command.TenantId, ticket.Id, actor, command.FileIds ?? [], cancellationToken);
        var note = SupportTicketNote.Create(noteId, command.TenantId, ticket.Id, actor, command.Text!, files, clock.UtcNow);
        notes.Add(note);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.TicketNoteAdded,
            SupportAuditActions.Resources.Ticket,
            ticket.Id.ToString(),
            "success",
            NoteFields,
            note.CreatedAt);
        // A race with the same clientId loses on PK_ticket_notes and answers 409 (SupportUnitOfWork, plan N15).
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: null, CancellationToken.None);
        return await SupportNoteDtos.ComposeAsync(members, viewer, command.TenantId, note, cancellationToken);
    }

    internal static ResourceConflictException ClientIdTaken() =>
        new("concurrency.conflict", "This client id already belongs to another note.");

    internal static SupportDomainException Closed() =>
        new(SupportErrorCodes.NoteTicketClosed, "A closed ticket takes no new notes.");

    /// <summary>
    /// Plan N18 and N19: the count and the repeats first (422 <c>support.note.too_many_files</c>, without asking Storage), then each
    /// file must be <c>Available</c> under the owner of (ticket, caller): another member's, another ticket's, a public form's, a pending
    /// or rejected one, or one that does not exist is 422 <c>support.note.file_invalid</c>, all the same. A file already in another note
    /// passes here and loses on <c>IX_ticket_note_files_file</c> at commit, with the same code. The snapshot keeps name, type and size
    /// (plan N17).
    /// </summary>
    private async Task<IReadOnlyList<SupportNoteFileSnapshot>> ResolveFilesAsync(
        Guid tenantId, SupportTicketId ticketId, Guid actor, IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count > SupportTicketNote.MaxFiles || fileIds.Distinct().Count() != fileIds.Count)
        {
            throw new SupportDomainException(
                SupportErrorCodes.NoteTooManyFiles, $"A note takes up to {SupportTicketNote.MaxFiles} different files.");
        }

        var ownerKey = SupportNoteUploads.OwnerKey(ticketId, actor);
        var files = new List<SupportNoteFileSnapshot>(fileIds.Count);
        foreach (var fileId in fileIds)
        {
            var file = await storage.FindAvailableAsync(tenantId, fileId, ownerKey, cancellationToken)
                ?? throw SupportNoteAttachmentRules.FileInvalid();
            files.Add(new SupportNoteFileSnapshot(file.FileId, file.Name, file.MimeType, file.SizeBytes));
        }

        return files;
    }
}

/// <summary>Maps notes to their contract, naming each author once per call.</summary>
internal static class SupportNoteDtos
{
    public static async Task<SupportTicketNoteDto> ComposeAsync(
        ISupportMemberDirectory members,
        ISupportViewer viewer,
        Guid tenantId,
        SupportTicketNote note,
        CancellationToken cancellationToken) =>
        (await ComposeAsync(members, viewer, tenantId, [note], cancellationToken))[0];

    /// <summary>Fase 4a (plan G8): el autor lleva <c>IsMe</c> contra quien pide.</summary>
    public static async Task<IReadOnlyList<SupportTicketNoteDto>> ComposeAsync(
        ISupportMemberDirectory members,
        ISupportViewer viewer,
        Guid tenantId,
        IReadOnlyList<SupportTicketNote> notes,
        CancellationToken cancellationToken)
    {
        var authors = notes.Select(note => note.AuthorMemberId).Distinct().ToList();
        IReadOnlyDictionary<Guid, string> names = authors.Count == 0
            ? new Dictionary<Guid, string>()
            : await members.NamesAsync(tenantId, authors, cancellationToken);
        var me = await viewer.MemberIdAsync(tenantId, cancellationToken);
        return
        [
            .. notes.Select(note => new SupportTicketNoteDto(
                note.Id,
                note.Text,
                SupportTicketDtoComposer.ToMemberDto(note.AuthorMemberId, names, me),
                note.CreatedAt,
                [.. note.Files.Select(file => new SupportNoteFileDto(file.FileId, file.FileName, file.MimeType, file.SizeBytes))])),
        ];
    }
}

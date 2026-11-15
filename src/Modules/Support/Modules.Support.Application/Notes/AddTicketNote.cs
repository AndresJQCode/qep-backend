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
/// Spec 2026-11-15 §5.4: an internal note. Order: authorization (<c>support.ticket.manage</c>) → active membership (403) → validator →
/// ticket (404) → a note with this <c>clientId</c> (plan N15: same ticket and author answers it, even on a closed ticket; anything else
/// is 409 <c>concurrency.conflict</c>) → closed ticket (422 <c>support.note.ticket_closed</c>) → files → note and audit → commit →
/// notice on the hub → 201.
///
/// The note does not go through the <see cref="SupportTicket"/> aggregate: no version bump, no <c>ticket_events</c> row, no
/// integration event, and nothing of it reaches <c>public/support</c>.
/// </summary>
public sealed class AddTicketNoteHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ISupportTicketRepository tickets,
    ISupportTicketNoteRepository notes,
    ISupportMemberDirectory members,
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
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var ticket = await tickets.FindAsync(command.TenantId, new SupportTicketId(command.TicketId), cancellationToken)
            ?? throw SupportNotFound.Ticket(command.TicketId);

        // Before the closed check: the retry of a note whose HTTP answer was lost gets its note even if the ticket closed since.
        var noteId = command.ClientId!.Value;
        var existing = await notes.FindAsync(command.TenantId, noteId, cancellationToken);
        if (existing is not null)
        {
            if (existing.TicketId != ticket.Id || existing.AuthorMemberId != actor)
            {
                throw ClientIdTaken();
            }

            return await SupportNoteDtos.ComposeAsync(members, command.TenantId, existing, cancellationToken);
        }

        if (ticket.Status is SupportTicketStatus.Closed)
        {
            throw Closed();
        }

        var files = ResolveFiles(command.FileIds);
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
        await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        return await SupportNoteDtos.ComposeAsync(members, command.TenantId, note, cancellationToken);
    }

    internal static ResourceConflictException ClientIdTaken() =>
        new("concurrency.conflict", "This client id already belongs to another note.");

    internal static SupportDomainException Closed() =>
        new(SupportErrorCodes.NoteTicketClosed, "A closed ticket takes no new notes.");

    // Files arrive with Task 5 of the plan; until then any file id is not a valid file.
    private static IReadOnlyList<SupportNoteFileSnapshot> ResolveFiles(IReadOnlyList<Guid>? fileIds) =>
        fileIds is null or { Count: 0 }
            ? []
            : throw new SupportDomainException(SupportErrorCodes.NoteFileInvalid, "The note file is not valid.");
}

/// <summary>Maps notes to their contract, naming each author once per call.</summary>
internal static class SupportNoteDtos
{
    public static async Task<SupportTicketNoteDto> ComposeAsync(
        ISupportMemberDirectory members, Guid tenantId, SupportTicketNote note, CancellationToken cancellationToken) =>
        (await ComposeAsync(members, tenantId, [note], cancellationToken))[0];

    public static async Task<IReadOnlyList<SupportTicketNoteDto>> ComposeAsync(
        ISupportMemberDirectory members, Guid tenantId, IReadOnlyList<SupportTicketNote> notes, CancellationToken cancellationToken)
    {
        var authors = notes.Select(note => note.AuthorMemberId).Distinct().ToList();
        IReadOnlyDictionary<Guid, string> names = authors.Count == 0
            ? new Dictionary<Guid, string>()
            : await members.NamesAsync(tenantId, authors, cancellationToken);
        return
        [
            .. notes.Select(note => new SupportTicketNoteDto(
                note.Id,
                note.Text,
                new SupportMemberDto(
                    note.AuthorMemberId,
                    names.TryGetValue(note.AuthorMemberId, out var name) ? name : SupportTicketDtoComposer.UnknownName),
                note.CreatedAt,
                [.. note.Files.Select(file => new SupportNoteFileDto(file.FileId, file.FileName, file.MimeType, file.SizeBytes))])),
        ];
    }
}

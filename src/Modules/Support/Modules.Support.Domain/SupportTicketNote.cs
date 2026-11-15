namespace Modules.Support.Domain;

/// <summary>
/// Spec 2026-11-15 §5.4: an internal note an advisor leaves on a ticket. It never reaches the requester (no public page, no
/// integration event, no <c>ticket_events</c> row) and it is not part of the <see cref="SupportTicket"/> aggregate: adding one does
/// not bump the ticket's version. Insert-only: nothing edits or deletes a note. Its <see cref="Id"/> is the screen's <c>clientId</c>
/// (plan N15), so a retry finds the note it already created.
/// </summary>
public sealed class SupportTicketNote
{
    /// <summary>Spec §5.4: up to five files per note (plan N19), its own cap, not the public form's.</summary>
    public const int MaxFiles = 5;

    private readonly List<SupportTicketNoteFile> _files = [];

    // EF Core materializes through here; Create is the only entry point for code.
    private SupportTicketNote()
    {
        Text = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    public Guid AuthorMemberId { get; private set; }

    public string Text { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>In the order they were sent (by <see cref="SupportTicketNoteFile.Position"/>).</summary>
    public IReadOnlyList<SupportTicketNoteFile> Files => [.. _files.OrderBy(file => file.Position)];

    /// <summary>
    /// The text follows <see cref="SupportTicketReply.IsValidText"/> (1 to 4 096 characters after trimming, no control characters but
    /// line feed, carriage return and tab) and is stored trimmed; otherwise <see cref="SupportErrorCodes.NoteTextInvalid"/>. More than
    /// <see cref="MaxFiles"/> files, or the same file twice, is <see cref="SupportErrorCodes.NoteTooManyFiles"/>. The API validator
    /// catches the text first (plan N16); this guard keeps the domain independent of the caller.
    /// </summary>
    public static SupportTicketNote Create(
        Guid id,
        Guid tenantId,
        SupportTicketId ticketId,
        Guid authorMemberId,
        string text,
        IReadOnlyList<SupportNoteFileSnapshot> files,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A note needs its id.", nameof(id));
        }

        if (!SupportTicketReply.IsValidText(text))
        {
            throw new SupportDomainException(
                SupportErrorCodes.NoteTextInvalid,
                $"The note text must have between 1 and {SupportTicketReply.TextMaxLength} characters and no control characters.");
        }

        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > MaxFiles || files.Select(file => file.FileId).Distinct().Count() != files.Count)
        {
            throw new SupportDomainException(
                SupportErrorCodes.NoteTooManyFiles, $"A note takes up to {MaxFiles} different files.");
        }

        var note = new SupportTicketNote
        {
            Id = id,
            TenantId = tenantId,
            TicketId = ticketId,
            AuthorMemberId = authorMemberId,
            Text = text.Trim(),
            CreatedAt = now,
        };
        for (var position = 0; position < files.Count; position++)
        {
            note._files.Add(SupportTicketNoteFile.Create(id, files[position], position));
        }

        return note;
    }
}

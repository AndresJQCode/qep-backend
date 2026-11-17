namespace Modules.Support.Domain;

/// <summary>What the note shows of a Storage file, taken when the note is created (plan N17): Support does not see Storage, so
/// listing notes never asks it for each file.</summary>
public sealed record SupportNoteFileSnapshot(Guid FileId, string FileName, string MimeType, long SizeBytes);

/// <summary>
/// A file of a <see cref="SupportTicketNote"/> (spec §5.4, plan N17). The file lives in Storage (<see cref="FileId"/>, a soft
/// reference to another schema); here stay the link, its position in the note and the snapshot. The unique index on
/// <see cref="FileId"/> (<c>IX_ticket_note_files_file</c>) keeps one file out of two notes. Insert-only, like the note.
/// </summary>
public sealed class SupportTicketNoteFile
{
    // EF Core materializes through here; Create is the only entry point for code.
    private SupportTicketNoteFile()
    {
        FileName = string.Empty;
        MimeType = string.Empty;
    }

    public Guid NoteId { get; private set; }

    public Guid FileId { get; private set; }

    /// <summary>0-based, in the order the screen sent the files.</summary>
    public int Position { get; private set; }

    public string FileName { get; private set; }

    public string MimeType { get; private set; }

    public long SizeBytes { get; private set; }

    internal static SupportTicketNoteFile Create(Guid noteId, SupportNoteFileSnapshot snapshot, int position)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.FileId == Guid.Empty)
        {
            throw new ArgumentException("A note file needs its file id.", nameof(snapshot));
        }

        var name = snapshot.FileName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > SupportPublicAttachmentRules.NameMaxLength)
        {
            throw new ArgumentException(
                $"The file name must have between 1 and {SupportPublicAttachmentRules.NameMaxLength} characters.", nameof(snapshot));
        }

        var mime = snapshot.MimeType?.Trim();
        if (string.IsNullOrEmpty(mime) || mime.Length > SupportPublicAttachmentRules.MimeTypeMaxLength)
        {
            throw new ArgumentException(
                $"The media type must have between 1 and {SupportPublicAttachmentRules.MimeTypeMaxLength} characters.", nameof(snapshot));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshot.SizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return new SupportTicketNoteFile
        {
            NoteId = noteId,
            FileId = snapshot.FileId,
            Position = position,
            FileName = name,
            MimeType = mime,
            SizeBytes = snapshot.SizeBytes,
        };
    }
}

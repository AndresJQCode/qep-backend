namespace Modules.Support.Domain;

/// <summary>
/// Un adjunto que la persona subió con el formulario público (plan F35). El archivo vive en Storage (<see cref="FileId"/>, referencia
/// blanda a otro esquema); acá queda el vínculo con el ticket y la foto de lo que se mostró: nombre, tipo y tamaño. Se inserta en la
/// misma transacción que el ticket. El índice único sobre <see cref="FileId"/> (<c>IX_ticket_attachments_file</c>) es lo que impide
/// que dos radicaciones reclamen el mismo archivo.
/// </summary>
public sealed class SupportTicketAttachment
{
    // EF Core materializa por acá; Attach es el único punto de entrada de código.
    private SupportTicketAttachment()
    {
        FileName = string.Empty;
        MimeType = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    public Guid FileId { get; private set; }

    public string FileName { get; private set; }

    public string MimeType { get; private set; }

    public long SizeBytes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static SupportTicketAttachment Attach(
        Guid id,
        Guid tenantId,
        SupportTicketId ticketId,
        Guid fileId,
        string fileName,
        string mimeType,
        long sizeBytes,
        DateTimeOffset now)
    {
        if (id == Guid.Empty || fileId == Guid.Empty)
        {
            throw new ArgumentException("An attachment needs its id and its file id.", nameof(id));
        }

        var name = fileName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > SupportPublicAttachmentRules.NameMaxLength)
        {
            throw new ArgumentException(
                $"The file name must have between 1 and {SupportPublicAttachmentRules.NameMaxLength} characters.", nameof(fileName));
        }

        var mime = mimeType?.Trim();
        if (string.IsNullOrEmpty(mime) || mime.Length > SupportPublicAttachmentRules.MimeTypeMaxLength)
        {
            throw new ArgumentException(
                $"The media type must have between 1 and {SupportPublicAttachmentRules.MimeTypeMaxLength} characters.", nameof(mimeType));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        return new SupportTicketAttachment
        {
            Id = id,
            TenantId = tenantId,
            TicketId = ticketId,
            FileId = fileId,
            FileName = name,
            MimeType = mime,
            SizeBytes = sizeBytes,
            CreatedAt = now,
        };
    }
}

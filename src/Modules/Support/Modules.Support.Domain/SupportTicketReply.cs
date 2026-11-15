namespace Modules.Support.Domain;

/// <summary>
/// Una respuesta del ticket al solicitante (spec §8.3, plan E16). Nace <see cref="SupportReplyStatus.Pending"/> y se confirma antes
/// de llamar afuera: su <see cref="Id"/> es el <c>clientId</c> que viaja a Mensajería, que es idempotente por él, así un reintento
/// no manda dos mensajes. <see cref="ClientId"/> es el de quien llama y es único por ticket. Sale de Pending una sola vez.
/// </summary>
public sealed class SupportTicketReply
{
    /// <summary>La regla de texto de <c>SendMessageValidator</c> de Mensajería: 1 a 4 096 caracteres tras recortar.</summary>
    public const int TextMaxLength = 4096;

    public const int FailureCodeMaxLength = 64;

    /// <summary>El largo del <c>wa_id</c> guardado de una plantilla al celular (columna <c>target_wa_id</c>).</summary>
    public const int TargetWaIdMaxLength = 32;

    /// <summary>El largo del nombre del adjunto: el de la sesión de subida de Mensajería (<c>SupportReplyAttachmentRules</c>).</summary>
    public const int FileNameMaxLength = 256;

    // EF Core materializa por acá; Start es el único punto de entrada de código.
    private SupportTicketReply()
    {
        Text = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    public Guid ClientId { get; private set; }

    public SupportReplyChannel Channel { get; private set; }

    public SupportReplyDelivery Delivery { get; private set; }

    public string Text { get; private set; }

    public SupportReplyStatus Status { get; private set; }

    public string? FailureCode { get; private set; }

    /// <summary>El mensaje de Mensajería, si salió por el hilo o por plantilla.</summary>
    public Guid? MessageId { get; private set; }

    /// <summary>La conexión de WhatsApp que eligió el asesor para una plantilla sin conversación (plan E14).</summary>
    public Guid? ConnectionId { get; private set; }

    /// <summary>Revisión final (I1): el <c>wa_id</c> al que sale una plantilla sin conversación, tomado del celular de la foto al crear la
    /// fila. Retomarla manda a este, no al celular que la foto tenga después.</summary>
    public string? TargetWaId { get; private set; }

    /// <summary>Revisión final (M2): el adjunto de una respuesta por el hilo (la sesión de subida de Mensajería y su nombre). Retomarla
    /// manda este, no el que traiga el reintento.</summary>
    public Guid? UploadId { get; private set; }

    public string? FileName { get; private set; }

    public Guid AuthorMemberId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>El correo sale sólo como <see cref="SupportReplyDelivery.Email"/>, y los canales de Mensajería nunca como correo.
    /// El texto llega validado por el validador de la API; acá se repite la regla para que el dominio no dependa de quien llama.
    /// <paramref name="targetWaId"/> va sólo con una plantilla; el adjunto (<paramref name="uploadId"/> con su <paramref name="fileName"/>,
    /// los dos o ninguno) sólo con texto por el hilo.</summary>
    public static SupportTicketReply Start(
        Guid tenantId,
        SupportTicketId ticketId,
        Guid clientId,
        SupportReplyChannel channel,
        SupportReplyDelivery delivery,
        string text,
        Guid? connectionId,
        Guid authorMemberId,
        DateTimeOffset now,
        string? targetWaId = null,
        Guid? uploadId = null,
        string? fileName = null)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("The client id is required.", nameof(clientId));
        }

        if ((channel is SupportReplyChannel.Email) != (delivery is SupportReplyDelivery.Email))
        {
            throw new ArgumentException($"A '{channel}' reply cannot go out as '{delivery}'.", nameof(delivery));
        }

        if (targetWaId is not null
            && (delivery is not SupportReplyDelivery.Template
                || string.IsNullOrWhiteSpace(targetWaId)
                || targetWaId.Trim().Length > TargetWaIdMaxLength))
        {
            throw new ArgumentException(
                $"A target wa_id goes only with a template and has between 1 and {TargetWaIdMaxLength} characters.", nameof(targetWaId));
        }

        if ((uploadId is null) != (fileName is null))
        {
            throw new ArgumentException("An attachment needs both its upload id and its file name.", nameof(uploadId));
        }

        if (uploadId is not null
            && (delivery is not SupportReplyDelivery.Text
                || uploadId == Guid.Empty
                || string.IsNullOrWhiteSpace(fileName)
                || fileName.Trim().Length > FileNameMaxLength))
        {
            throw new ArgumentException(
                $"An attachment goes only with a text reply, with a file name of up to {FileNameMaxLength} characters.", nameof(uploadId));
        }

        return new SupportTicketReply
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TicketId = ticketId,
            ClientId = clientId,
            Channel = channel,
            Delivery = delivery,
            Text = ValidText(text),
            Status = SupportReplyStatus.Pending,
            ConnectionId = connectionId,
            TargetWaId = targetWaId?.Trim(),
            UploadId = uploadId,
            FileName = fileName?.Trim(),
            AuthorMemberId = authorMemberId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>La regla de <c>SendMessageValidator</c>: no vacío tras recortar, como mucho <see cref="TextMaxLength"/>, sin
    /// caracteres de control salvo salto de línea, retorno y tabulador.</summary>
    public static bool IsValidText(string? text)
    {
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= TextMaxLength
            && !trimmed.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'));
    }

    public void MarkSent(Guid? messageId, DateTimeOffset now)
    {
        EnsurePending();
        Status = SupportReplyStatus.Sent;
        MessageId = messageId;
        UpdatedAt = now;
    }

    /// <summary>Revisión final (M4): <paramref name="messageId"/> es el mensaje que Mensajería registró como fallido, si lo hay. Queda en
    /// la fila para ubicarlo en el hilo; quien llama no lo fija en el expediente.</summary>
    public void MarkFailed(string code, Guid? messageId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > FailureCodeMaxLength)
        {
            throw new ArgumentException($"The failure code must have between 1 and {FailureCodeMaxLength} characters.", nameof(code));
        }

        EnsurePending();
        Status = SupportReplyStatus.Failed;
        FailureCode = code;
        MessageId = messageId;
        UpdatedAt = now;
    }

    private void EnsurePending()
    {
        if (Status is not SupportReplyStatus.Pending)
        {
            throw new InvalidOperationException($"The reply '{Id}' is already '{Status}'.");
        }
    }

    private static string ValidText(string text)
    {
        if (!IsValidText(text))
        {
            throw new ArgumentException(
                $"The reply text must have between 1 and {TextMaxLength} characters and no control characters.", nameof(text));
        }

        return text.Trim();
    }
}

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

    public Guid AuthorMemberId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>El correo sale sólo como <see cref="SupportReplyDelivery.Email"/>, y los canales de Mensajería nunca como correo.
    /// El texto llega validado por el validador de la API; acá se repite la regla para que el dominio no dependa de quien llama.</summary>
    public static SupportTicketReply Start(
        Guid tenantId,
        SupportTicketId ticketId,
        Guid clientId,
        SupportReplyChannel channel,
        SupportReplyDelivery delivery,
        string text,
        Guid? connectionId,
        Guid authorMemberId,
        DateTimeOffset now)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("The client id is required.", nameof(clientId));
        }

        if ((channel is SupportReplyChannel.Email) != (delivery is SupportReplyDelivery.Email))
        {
            throw new ArgumentException($"A '{channel}' reply cannot go out as '{delivery}'.", nameof(delivery));
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

    public void MarkFailed(string code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > FailureCodeMaxLength)
        {
            throw new ArgumentException($"The failure code must have between 1 and {FailureCodeMaxLength} characters.", nameof(code));
        }

        EnsurePending();
        Status = SupportReplyStatus.Failed;
        FailureCode = code;
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

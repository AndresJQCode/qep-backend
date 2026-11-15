namespace Modules.Support.Domain;

/// <summary>
/// Un mensaje del hilo de Mensajería vinculado a un ticket (spec §8.2, plan E9). Guarda sólo ids: el texto vive en
/// <c>messaging.messages</c> y se lee al mostrar. <see cref="OccurredAt"/> es la hora del mensaje, no la del vínculo: el expediente
/// se ordena por ella. Un mismo mensaje tiene como mucho una fila por ticket (índice único <c>(ticket_id, message_id)</c>).
/// </summary>
public sealed class SupportTicketMessage
{
    // EF Core materializa por acá; Link es el único punto de entrada de código.
    private SupportTicketMessage()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid MessageId { get; private set; }

    public SupportTicketMessageState State { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Nace <see cref="SupportTicketMessageState.Linked"/> (el seguimiento) o <see cref="SupportTicketMessageState.Pinned"/>
    /// (el asesor). Un vínculo no nace soltado: soltar es una acción sobre uno que ya existe.</summary>
    public static SupportTicketMessage Link(
        Guid tenantId,
        SupportTicketId ticketId,
        Guid conversationId,
        Guid messageId,
        DateTimeOffset occurredAt,
        SupportTicketMessageState state,
        DateTimeOffset now)
    {
        if (state is SupportTicketMessageState.Released)
        {
            throw new ArgumentException("A message link cannot start released.", nameof(state));
        }

        if (conversationId == Guid.Empty || messageId == Guid.Empty)
        {
            throw new ArgumentException("The conversation and the message are required.");
        }

        return new SupportTicketMessage
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TicketId = ticketId,
            ConversationId = conversationId,
            MessageId = messageId,
            State = state,
            OccurredAt = occurredAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Desde vinculado o soltado: el asesor puede volver a fijar lo que soltó. Fijar uno ya fijado no hace nada.</summary>
    public void Pin(DateTimeOffset now) => MoveTo(SupportTicketMessageState.Pinned, now);

    /// <summary>Lo saca del expediente; el seguimiento del hilo nunca lo vuelve a vincular. Soltar uno ya soltado no hace nada.</summary>
    public void Release(DateTimeOffset now) => MoveTo(SupportTicketMessageState.Released, now);

    private void MoveTo(SupportTicketMessageState state, DateTimeOffset now)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        UpdatedAt = now;
    }
}

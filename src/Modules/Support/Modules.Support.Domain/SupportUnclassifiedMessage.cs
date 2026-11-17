namespace Modules.Support.Domain;

/// <summary>
/// Un entrante que cayó en dos o más tickets abiertos de la misma conversación y nadie ha clasificado (spec §8.2, plan E5). La
/// clave es el mensaje (<c>message_id</c>): un entrante se registra una sola vez, y esa PK es la que frena a dos réplicas del
/// seguimiento (plan E3). Una vez resuelta, la fila no cambia más.
/// </summary>
public sealed class SupportUnclassifiedMessage
{
    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private SupportUnclassifiedMessage()
    {
    }

    public Guid MessageId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Null mientras está pendiente.</summary>
    public SupportUnclassifiedResolution? Resolution { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>La membresía que la resolvió; null cuando pasó sola al único candidato.</summary>
    public Guid? ResolvedByMemberId { get; private set; }

    public SupportTicketId? AssignedTicketId { get; private set; }

    public bool IsPending => Resolution is null;

    public static SupportUnclassifiedMessage Create(
        Guid tenantId, Guid conversationId, Guid messageId, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (conversationId == Guid.Empty || messageId == Guid.Empty)
        {
            throw new ArgumentException("The conversation and the message are required.");
        }

        return new SupportUnclassifiedMessage
        {
            MessageId = messageId,
            TenantId = tenantId,
            ConversationId = conversationId,
            OccurredAt = occurredAt,
            CreatedAt = now,
        };
    }

    /// <summary>Con <paramref name="actorMemberId"/> la eligió un asesor (<see cref="SupportUnclassifiedResolution.Assigned"/>);
    /// sin él, el seguimiento la pasó al único candidato que quedó (<see cref="SupportUnclassifiedResolution.Reassigned"/>).</summary>
    public void Assign(SupportTicketId ticketId, Guid? actorMemberId, DateTimeOffset now)
    {
        Resolve(actorMemberId is null ? SupportUnclassifiedResolution.Reassigned : SupportUnclassifiedResolution.Assigned,
            actorMemberId, now);
        AssignedTicketId = ticketId;
    }

    /// <summary>«De ninguno» (plan E8).</summary>
    public void Dismiss(Guid actorMemberId, DateTimeOffset now) =>
        Resolve(SupportUnclassifiedResolution.Dismissed, actorMemberId, now);

    private void Resolve(SupportUnclassifiedResolution resolution, Guid? actorMemberId, DateTimeOffset now)
    {
        if (!IsPending)
        {
            throw new InvalidOperationException($"The unclassified message '{MessageId}' is already resolved as '{Resolution}'.");
        }

        Resolution = resolution;
        ResolvedAt = now;
        ResolvedByMemberId = actorMemberId;
    }
}

using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>§8.3: la fila que se intenta insertar. <c>Text</c> ya viene recortado; <c>Now</c> es la hora del
/// servidor al reclamar. <c>ReplyToMessageId</c>/<c>ReplyToWamid</c>: el citado ya validado (spec 2026-10-10 §8.6).</summary>
public sealed record OutboundDraft(
    Guid MessageId, Guid ConversationId, Guid TenantId, Guid ConnectionId, Guid ClientId, string Text, Guid SentByMemberId,
    DateTimeOffset Now, Guid? ReplyToMessageId = null, string? ReplyToWamid = null);

/// <summary>La fila que ya existía con ese <c>clientId</c> en la conversación (con <c>FOR UPDATE</c> en el reclamo).</summary>
public sealed record ExistingOutbound(Guid Id, MessageStatus Status, DateTimeOffset OccurredAt, Guid? SentByMemberId, string Text, Guid? ReplyToMessageId = null);

/// <summary>
/// §8.3, «Idempotencia sin que la fila a medio enviar se vea»: una transacción abierta con la fila del
/// mensaje insertada (o reclamada con FOR UPDATE si ya existía con ese clientId). Un segundo request con
/// el mismo clientId espera en el INSERT hasta que esta transacción termine. Se cierra con uno de los
/// tres: commit Sent (con wamid y foto de la conversación), commit Failed, o rollback (nada se guarda).
/// Desecharlo sin cerrar es rollback. Reenviar un Failed reescribe el texto guardado con el del request nuevo.
/// </summary>
public interface IOutboundClaim : IAsyncDisposable
{
    bool Inserted { get; }

    /// <summary>El id de la fila: el del borrador si se insertó, el de la existente si no.</summary>
    Guid MessageId { get; }

    ExistingOutbound? Existing { get; }

    Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, CancellationToken cancellationToken);

    Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-09 §8.3: el reclamo idempotente de un saliente por <c>(conversation_id, client_id)</c>.</summary>
public interface IOutboundMessages
{
    Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-10 §8.5 paso 2: el mensaje ya commiteado con ese clientId, sin candado; <c>null</c> si no hay.</summary>
    Task<ExistingOutbound?> FindByClientIdAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken);
}

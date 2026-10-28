using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-11 §6.5: el medio de un saliente, ya en R2. Sin clave: la calcula el reclamo con su
/// <c>MessageId</c> (<see cref="MessagingMediaKeys.For"/>), que en un reintento de un <c>Failed</c> no es el del
/// borrador. <c>Sha256Hex</c> en minúsculas, como la copia de los entrantes; <c>null</c> si la subida a Meta cortó a mitad y no hay
/// hash completo (la columna lo admite). <c>FileName</c>: el nombre real que ve la persona (<c>null</c> en una nota de voz).</summary>
public sealed record OutboundMedia(string MimeType, long SizeBytes, string? Sha256Hex, string? FileName = null);

/// <summary>§8.3: la fila que se intenta insertar. <c>Text</c> ya viene recortado (<c>null</c> en una nota de voz);
/// <c>Now</c> es la hora del servidor al reclamar. <c>ReplyToMessageId</c>/<c>ReplyToWamid</c>: el citado ya validado
/// (spec 2026-10-10 §8.6). <c>Kind</c> y <c>Media</c>: spec 2026-10-11 §6.5; el texto sigue con <c>Text</c> y sin medio.
/// <c>SentByChatbotId</c> (spec 2026-10-21 D-I3): si viene, el autor es un chatbot y el reclamo escribe <c>sent_by_kind = 2</c> con
/// <c>SentByMemberId</c> nulo, como lo exigen los CHECK de la tabla.
/// <c>Caption</c> (messaging-media-reply R11): el pie de una imagen o un documento, en <c>messages.caption</c> y no en
/// <c>message_media</c>, como lo guarda la ingesta de los entrantes.</summary>
public sealed record OutboundDraft(
    Guid MessageId, Guid ConversationId, Guid TenantId, Guid ConnectionId, Guid ClientId, string? Text, Guid? SentByMemberId,
    DateTimeOffset Now, Guid? ReplyToMessageId = null, string? ReplyToWamid = null, MessageKind Kind = MessageKind.Text, OutboundMedia? Media = null,
    string? DetailsJson = null, string? Caption = null, Guid? SentByChatbotId = null);

/// <summary>La fila que ya existía con ese <c>clientId</c> en la conversación (con <c>FOR UPDATE</c> en el reclamo).
/// <c>Kind</c> y <c>MediaMimeType</c> (spec 2026-10-11 §6.5): la idempotencia devuelve la burbuja de audio con su
/// <c>media.url</c>. <c>DetailsJson</c> (spec 2026-10-11 §5.2): la plantilla de un mensaje <c>Template</c>, para devolver lo guardado.
/// <c>MediaFileName</c> y <c>Caption</c> (messaging-media-reply R5): repetir un clientId devuelve la burbuja con su nombre y su pie.</summary>
public sealed record ExistingOutbound(
    Guid Id, MessageStatus Status, DateTimeOffset OccurredAt, Guid? SentByMemberId, string Text, Guid? ReplyToMessageId = null,
    MessageKind Kind = MessageKind.Text, string? MediaMimeType = null, string? DetailsJson = null, string? MediaFileName = null, string? Caption = null, Guid? SentByChatbotId = null);

/// <summary>
/// §8.3, «Idempotencia sin que la fila a medio enviar se vea»: una transacción abierta con la fila del
/// mensaje insertada (o reclamada con FOR UPDATE si ya existía con ese clientId). Un segundo request con
/// el mismo clientId espera en el INSERT hasta que esta transacción termine. Se cierra con uno de los
/// tres: commit Sent (con wamid y foto de la conversación), commit Failed, o rollback (nada se guarda).
/// Desecharlo sin cerrar es rollback. Reenviar un Failed reescribe el texto guardado con el del request nuevo
/// y, si trae medio, la fila de <c>message_media</c> (spec 2026-10-11 §6.5).
/// </summary>
public interface IOutboundClaim : IAsyncDisposable
{
    bool Inserted { get; }

    /// <summary>El id de la fila: el del borrador si se insertó, el de la existente si no.</summary>
    Guid MessageId { get; }

    /// <summary>La conversación del borrador: la que el aviso en tiempo real nombra al consolidar (spec 2026-10-14).</summary>
    Guid ConversationId { get; }

    ExistingOutbound? Existing { get; }

    /// <summary><paramref name="metaMediaId"/>: el id del medio en Meta, si el borrador trae medio (spec 2026-10-11 §6.5).</summary>
    Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken);

    /// <summary>Con medio, también escribe <c>message_media</c> (D-V1); <paramref name="metaMediaId"/> <c>null</c> si la
    /// subida a Meta falló (se guarda <c>''</c>, D-V2).</summary>
    Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-09 §8.3: el reclamo idempotente de un saliente por <c>(conversation_id, client_id)</c>.</summary>
public interface IOutboundMessages
{
    Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-10 §8.5 paso 2: el mensaje ya commiteado con ese clientId, sin candado; <c>null</c> si no hay.</summary>
    Task<ExistingOutbound?> FindByClientIdAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken);
}

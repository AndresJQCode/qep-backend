using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-09: el mensaje programado con tracking. Los handlers de usuario lo leen sin candado (<c>Find*</c>) o lo toman con
/// <see cref="LockAsync"/> para cambiarlo. Los dos únicos de la tabla (uno activo por conversación, un <c>clientId</c> por conversación)
/// salen de <see cref="AddAsync"/> como <c>ResourceConflictException(ScheduledAlreadyExists)</c>; el handler de crear relee por
/// <c>clientId</c> para resolver la carrera del mismo reintento.</summary>
public interface IScheduledMessageRepository
{
    /// <summary>Sin tracking.</summary>
    Task<ScheduledMessage?> FindByClientIdAsync(Guid tenantId, Guid conversationId, Guid clientId, CancellationToken cancellationToken);

    /// <summary>El programado activo (<c>Scheduled</c>, <c>Sending</c> o <c>Failed</c>) de la conversación, sin tracking.</summary>
    Task<ScheduledMessage?> FindActiveAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Abre una transacción y toma la fila con <c>FOR UPDATE</c>; <c>null</c> si no existe. Soltarlo sin confirmar es rollback.</summary>
    Task<IScheduledMessageLock?> LockAsync(Guid tenantId, Guid conversationId, Guid scheduledMessageId, CancellationToken cancellationToken);

    /// <summary>Guarda de inmediato (por <c>MessagingSaveChanges</c>, que traduce los únicos).</summary>
    Task AddAsync(ScheduledMessage message, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-11-09 «Worker y ejecución»: un programado que el worker pasó a <c>Sending</c> con su lease. <c>Attempts</c> es el valor
/// tras el reclamo: el cierre lo usa como compare-and-set, así que un lease vencido y recuperado por otro no se pisa. <c>LockedUntil</c> es el
/// vencimiento de <b>este</b> lease: la recuperación lo renueva sin sumar intentos, así que el reclamo del mensaje lo compara con la fila para
/// saber que el lease sigue siendo suyo (<see cref="ScheduledLease"/>).
/// <c>MediaSizeBytes</c> y <c>MediaMimeType</c> son los del objeto guardado (la imagen ya comprimida).</summary>
public sealed record ClaimedScheduledMessage(
    Guid Id, Guid TenantId, Guid ConversationId, Guid ClientId, MessageKind Kind, bool IsVoiceNote, string? Text, string? Caption,
    string? MediaStorageKey, string? MediaMimeType, string? MediaFileName, long? MediaSizeBytes, string? MediaSha256, Guid AuthorMemberId,
    int Attempts, DateTimeOffset LockedUntil);

/// <summary>Un objeto de <c>messaging/scheduled/</c> de una fila terminal que no se pudo borrar al transicionar.</summary>
public sealed record PurgeableScheduledMedia(Guid TenantId, Guid Id, string StorageKey);

/// <summary>
/// Spec 2026-11-09: el SQL del worker de programados, cross-tenant y sin tracking. Los reclamos usan <c>FOR UPDATE SKIP LOCKED</c>, ponen
/// <c>Sending</c> con <c>locked_until = now + lease</c> y suben <c>attempts</c>. Los cierres son compare-and-set por
/// <c>(id, status = 'Sending', attempts)</c> y escriben la auditoría <c>.sent</c> / <c>.failed</c> (<c>bySystem</c>) en la misma
/// transacción; <c>false</c> = el CAS no tocó filas y no se escribió nada.
/// </summary>
public interface IScheduledMessageStore
{
    Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimDueAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Los <c>Sending</c> con el lease vencido (D9), con un lease nuevo.</summary>
    Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimExpiredAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);

    Task<bool> CompleteSentAsync(ClaimedScheduledMessage message, Guid sentMessageId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary><paramref name="failureCode"/> es el código de texto del programado (<c>messaging.…</c>), nunca el número de Meta que guarda
    /// <c>messages.failure_code</c>.</summary>
    Task<bool> CompleteFailedAsync(ClaimedScheduledMessage message, string failureCode, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<PurgeableScheduledMedia>> ListPurgeableAsync(int take, CancellationToken cancellationToken);

    /// <summary>Pone <c>media_storage_key</c> en <c>null</c> después de borrar el objeto.</summary>
    Task ClearMediaKeyAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}

/// <summary>La fila de un programado bloqueada dentro de una transacción, con la misma forma que <see cref="IOutboundClaim"/>.</summary>
public interface IScheduledMessageLock : IAsyncDisposable
{
    ScheduledMessage Message { get; }

    /// <summary>Guarda lo cambiado en <see cref="Message"/> y cierra la transacción.</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Como <see cref="CommitAsync"/>, y en la misma transacción quema el reintento del mensaje <c>Failed</c> que dejó el
    /// programado: así el hilo no puede reenviar un <c>clientId</c> descartado.</summary>
    Task CommitDiscardAsync(CancellationToken cancellationToken);
}

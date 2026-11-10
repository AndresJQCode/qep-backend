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

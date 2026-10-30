using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Coexistence;

/// <summary>
/// Spec 2026-10-29: consume del outbox de plataforma el evento de una conexión de WhatsApp por coexistencia y deja la fila
/// <c>Pending</c> de su importación de historial (<c>messaging.history_syncs</c>). La fila (SQL crudo) y la marca del inbox (SaveChanges) van en la misma
/// transacción explícita: un mensaje procesado nunca queda sin marcar, ni uno marcado sin su efecto. Singleton: el estado es del proceso.
/// La curva de reintentos es la de <c>QuotationEventsProcessor</c> (5 s, 30 s, 5 minutos). Messaging no referencia Integrations:
/// el nombre del evento y el payload (camelCase) son contrato y se leen por nombre.
/// </summary>
internal sealed partial class CoexistenceEventsProcessor(IServiceScopeFactory scopeFactory, ILogger<CoexistenceEventsProcessor> logger)
{
    internal const string Consumer = "messaging.coexistence-events";
    internal const string ConnectedEvent = "integrations.whatsapp-coexistence-connected.v1";

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    internal ClaimedOutboxConsumer Claims { get; } = new(scopeFactory, Consumer, [ConnectedEvent], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messaging could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messaging failed to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Messaging keeps failing to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    public Task ProcessBatchAsync(CancellationToken cancellationToken) =>
        Claims.ProcessBatchAsync(ProcessAsync, LogFailure, cancellationToken);

    private async Task ProcessAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var record = message.Record;
        // El insert crudo y el SaveChanges que marca el inbox comparten transacción: o quedan los dos, o ninguno.
        await using var transaction = await message.DbContext.Database.BeginTransactionAsync(cancellationToken);
        if (CoexistenceEventPayload.TryRead(record.PayloadJson, out var tenantId, out var connectionId, out var connectedAt))
        {
            // Un reintento o un evento repetido no pisa la fila que ya existe: la hora de conexión es la del primero.
            var sync = HistorySync.Start(tenantId, connectionId, connectedAt, message.Now);
            var pending = nameof(HistorySyncRequestState.Pending);
            await message.DbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO messaging.history_syncs
                    (connection_id, tenant_id, connected_at, contacts_state, history_state, attempts, created_at, updated_at, version)
                VALUES
                    ({sync.ConnectionId}, {sync.TenantId}, {sync.ConnectedAt}, {pending}, {pending}, 0, {sync.CreatedAt}, {sync.UpdatedAt}, {sync.Version})
                ON CONFLICT (connection_id) DO NOTHING
                """,
                cancellationToken);
        }
        else
        {
            LogMalformed(logger, record.Id, record.EventName);
        }

        await Claims.MarkProcessedAsync(message, cancellationToken);
        await MessagingSaveChanges.SaveAsync(message.DbContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    private void LogFailure(Exception exception, MessagingOutboxRecord record, int? attempts)
    {
        if (attempts is not { } attempt)
        {
            LogClaimFailed(logger, exception, record.Id);
            return;
        }

        if (attempt >= ErrorAfterAttempts)
        {
            LogAttemptFailedRepeatedly(logger, exception, record.Id, record.EventName, attempt);
        }
        else
        {
            LogAttemptFailed(logger, exception, record.Id, record.EventName, attempt);
        }
    }
}

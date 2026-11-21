using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Purchases.Application;
using Modules.Purchases.Infrastructure.Persistence;

namespace Modules.Purchases.Infrastructure.Messaging;

/// <summary>
/// Mayorista fase 3: consume del outbox de plataforma la aprobación y la anulación de un pedido mayorista y delega en
/// <see cref="WholesaleOrderEventApplier"/>. Por mensaje abre la transacción del <see cref="PurchasesDbContext"/>: el efecto
/// (orden, productos creados en Catalog —que se enrola en esta misma transacción—, auditoría) y el inbox se confirman juntos.
/// Un mensaje procesado nunca queda sin marcar, ni uno marcado sin su efecto.
/// <para><see cref="WholesaleEventOutcome.RetryLater"/> (ruling X8b: la orden sigue en borrador porque el envío no terminó de
/// guardarse) no marca nada: la transacción se deshace y el mensaje vuelve cuando su reclamo venza.</para>
/// <para>Payload malformado: procesado sin efecto y registrado, como Pipeline. Singleton: el estado es del proceso. La curva es la
/// de Pipeline (5 s, 30 s, 5 minutos).</para>
/// </summary>
internal sealed partial class WholesaleOrderEventsProcessor(IServiceScopeFactory scopeFactory, ILogger<WholesaleOrderEventsProcessor> logger)
{
    internal const string Consumer = "purchases.wholesale-order-events";
    internal const string ApprovedEvent = "quotations.order.wholesale-approved.v1";
    internal const string CancelledEvent = "quotations.order.wholesale-cancelled.v1";

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    /// <summary>
    /// Revisión del bloque C, M1: un <see cref="WholesaleEventOutcome.RetryLater"/> es normal unos minutos (el envío está
    /// terminando de guardarse), así que va en Information. Pasado el intento 12 —unos 50 minutos con la curva: 5 s, 30 s y
    /// diez de 5 minutos— la orden casi seguro quedó en borrador con el pedido ya creado (la ventana de D-8) y alguien tiene que
    /// mirarla: sube a Warning. El mensaje sigue reintentándose; nunca se descarta.
    /// </summary>
    internal const int RetryLaterWarningAfterAttempts = 12;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    internal ClaimedOutboxConsumer Claims { get; } =
        new(scopeFactory, Consumer, [ApprovedEvent, CancelledEvent], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {MessageId} ({EventName}) is not ready for its purchase order on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogRetryLater(ILogger logger, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) is still not ready for its purchase order on attempt {Attempt}; the order is probably a draft whose wholesale order already exists. It keeps being retried.")]
    private static partial void LogRetryLaterTooLong(ILogger logger, Guid messageId, string eventName, int attempt);

    internal static LogLevel RetryLaterLevel(int attempt) =>
        attempt > RetryLaterWarningAfterAttempts ? LogLevel.Warning : LogLevel.Information;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purchases could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purchases failed to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Purchases keeps failing to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    public Task<bool> ProcessBatchAsync(CancellationToken cancellationToken) =>
        Claims.ProcessBatchAsync(ProcessAsync, LogFailure, cancellationToken);

    private async Task ProcessAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var unitOfWork = message.Services.GetRequiredService<IPurchasesUnitOfWork>();
        var applier = message.Services.GetRequiredService<WholesaleOrderEventApplier>();
        var record = message.Record;

        // El reclamo ya se commiteó solo; desde acá todo va en una transacción. Sin Commit, el Dispose la deshace.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        WholesaleEventOutcome outcome;
        switch (record.EventName)
        {
            case ApprovedEvent when WholesaleOrderEventPayloads.TryReadApproved(record.PayloadJson, out var approval):
                outcome = await applier.ApplyApprovedAsync(approval, message.Now, cancellationToken);
                break;
            case CancelledEvent when WholesaleOrderEventPayloads.TryReadCancelled(record.PayloadJson, out var cancellation):
                outcome = await applier.ApplyCancelledAsync(cancellation, message.Now, cancellationToken);
                break;
            default:
                LogMalformed(logger, record.Id, record.EventName);
                outcome = WholesaleEventOutcome.Ignored;
                break;
        }

        if (outcome == WholesaleEventOutcome.RetryLater)
        {
            if (RetryLaterLevel(message.Attempts) == LogLevel.Warning)
            {
                LogRetryLaterTooLong(logger, record.Id, record.EventName, message.Attempts);
            }
            else
            {
                LogRetryLater(logger, record.Id, record.EventName, message.Attempts);
            }

            return;
        }

        await Claims.MarkProcessedAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Spec 2026-11-21: las fotos de los productos creados se copian ya confirmado todo. Nunca lanza por la foto.
        await applier.CopyPendingImagesAsync(message.Now, cancellationToken);
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    private void LogFailure(Exception exception, PurchasesOutboxRecord record, int? attempts)
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

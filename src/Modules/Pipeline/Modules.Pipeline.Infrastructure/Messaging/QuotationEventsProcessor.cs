using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Pipeline.Application;
using Modules.Pipeline.Infrastructure.Persistence;

namespace Modules.Pipeline.Infrastructure.Messaging;

/// <summary>
/// Spec 2026-10-13 D6: consume del outbox de plataforma los tres eventos del ciclo de una cotización y delega en
/// <see cref="QuotationEventApplier"/>. El efecto (negocio, foto, historial y auditoría) y el inbox se guardan en el mismo
/// SaveChanges: un mensaje procesado nunca queda sin marcar, ni uno marcado sin su efecto. Singleton: el estado es del proceso.
/// La curva es la de <c>SessionRevocationWorker</c> (5 s, 30 s, 5 minutos): la falla más probable es una carrera de versión con
/// una edición del asesor, que se resuelve en el reintento.
/// </summary>
internal sealed partial class QuotationEventsProcessor(IServiceScopeFactory scopeFactory, ILogger<QuotationEventsProcessor> logger)
{
    internal const string Consumer = "pipeline.quotation-events";
    internal const string SentEvent = "quotations.quotation.sent.v1";
    internal const string ConvertedEvent = "quotations.quotation.converted.v1";
    internal const string OrderCancelledEvent = "quotations.order.cancelled.v1";

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    internal ClaimedOutboxConsumer Claims { get; } =
        new(scopeFactory, Consumer, [SentEvent, ConvertedEvent, OrderCancelledEvent], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline failed to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline keeps failing to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    public Task ProcessBatchAsync(CancellationToken cancellationToken) =>
        Claims.ProcessBatchAsync(ProcessAsync, LogFailure, cancellationToken);

    private async Task ProcessAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var applier = message.Services.GetRequiredService<QuotationEventApplier>();
        var record = message.Record;
        var readable = true;
        switch (record.EventName)
        {
            case OrderCancelledEvent when QuotationEventPayloads.TryReadOrderCancelled(record.PayloadJson, out var cancelled):
                await applier.ApplyOrderCancelledAsync(cancelled, message.Now, cancellationToken);
                break;
            case SentEvent when QuotationEventPayloads.TryReadQuotation(record.PayloadJson, out var sent):
                await applier.ApplySentAsync(sent, message.Now, cancellationToken);
                break;
            case ConvertedEvent when QuotationEventPayloads.TryReadQuotation(record.PayloadJson, out var converted):
                await applier.ApplyConvertedAsync(converted, message.Now, cancellationToken);
                break;
            default:
                readable = false;
                break;
        }

        if (!readable)
        {
            LogMalformed(logger, record.Id, record.EventName);
        }

        await Claims.MarkProcessedAsync(message, cancellationToken);
        await message.Services.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(cancellationToken);
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    private void LogFailure(Exception exception, PipelineOutboxRecord record, int? attempts)
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

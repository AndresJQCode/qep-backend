using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Persistence;

namespace Modules.Shopify.Infrastructure.Messaging;

/// <summary>
/// Spec 2026-10-26 D-C6: consume del outbox de plataforma los eventos de los sinónimos de ciudad que publica Geography y delega en
/// <see cref="ShopifyCitySynonymEventApplier"/>, que siembra los trabajos. Los trabajos y el inbox se guardan en el mismo
/// SaveChanges: un mensaje procesado nunca queda sin sus trabajos, ni uno con trabajos sin marcar. Singleton: el estado es del
/// proceso. La curva es la de <c>QuotationEventsProcessor</c> (5 s, 30 s, 5 minutos).
/// </summary>
internal sealed partial class CitySynonymEventsProcessor(IServiceScopeFactory scopeFactory, ILogger<CitySynonymEventsProcessor> logger)
{
    internal const string Consumer = "shopify.city-synonym-events";
    internal const string ResolvedEvent = "geography.city_synonym.resolved.v1";
    internal const string CorrectedEvent = "geography.city_synonym.corrected.v1";

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    internal ShopifyClaimedOutboxConsumer Claims { get; } =
        new(scopeFactory, Consumer, [ResolvedEvent, CorrectedEvent], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify failed to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify keeps failing to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    public Task ProcessBatchAsync(CancellationToken cancellationToken) =>
        Claims.ProcessBatchAsync(ProcessAsync, LogFailure, cancellationToken);

    private async Task ProcessAsync(ShopifyClaimedMessage message, CancellationToken cancellationToken)
    {
        var applier = message.Services.GetRequiredService<ShopifyCitySynonymEventApplier>();
        var record = message.Record;
        var readable = true;
        switch (record.EventName)
        {
            case ResolvedEvent when CitySynonymEventPayloads.TryReadResolved(record.PayloadJson, out var resolved):
                await applier.ApplyResolvedAsync(record.Id, resolved, cancellationToken);
                break;
            case CorrectedEvent when CitySynonymEventPayloads.TryReadCorrected(record.PayloadJson, out var corrected):
                await applier.ApplyCorrectedAsync(record.Id, corrected, cancellationToken);
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
        await message.Services.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(cancellationToken);
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    private void LogFailure(Exception exception, ShopifyOutboxRecord record, int? attempts)
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

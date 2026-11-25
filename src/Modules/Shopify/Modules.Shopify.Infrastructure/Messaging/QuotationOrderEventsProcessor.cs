using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Persistence;

namespace Modules.Shopify.Infrastructure.Messaging;

/// <summary>
/// Spec 2026-11-24 §11.1.1: consume del outbox de plataforma los eventos de pedido que publica Quotations y delega en
/// <see cref="ShopifyShippingPartyChangedApplier"/>, que encola el envío. El envío y el inbox se guardan en el mismo SaveChanges:
/// un mensaje procesado nunca queda sin su envío, ni uno con envío sin marcar. Si otro guardado movió el envío abierto o la fila a
/// la vez, el guardado choca y el mensaje vuelve. Singleton: el estado es del proceso. La curva es la de
/// <see cref="CitySynonymEventsProcessor"/> (5 s, 30 s, 5 minutos).
/// </summary>
internal sealed partial class QuotationOrderEventsProcessor(IServiceScopeFactory scopeFactory, ILogger<QuotationOrderEventsProcessor> logger)
{
    internal const string Consumer = "shopify.quotation-order-events";
    internal const string ShippingPartyChangedEvent = ShopifyQuotationOrderEventNames.ShippingPartyChanged;

    /// <summary>Desde qué intento una falla se loguea en Error: el primero que deja la espera de 5 minutos por delante.</summary>
    internal const int ErrorAfterAttempts = 3;

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    internal ShopifyClaimedOutboxConsumer Claims { get; } =
        new(scopeFactory, Consumer, [ShippingPartyChangedEvent], RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify failed to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shopify keeps failing to apply outbox message {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    public Task<bool> ProcessBatchAsync(CancellationToken cancellationToken) =>
        Claims.ProcessBatchAsync(ProcessAsync, LogFailure, cancellationToken);

    private async Task ProcessAsync(ShopifyClaimedMessage message, CancellationToken cancellationToken)
    {
        var record = message.Record;
        if (QuotationOrderEventPayloads.TryReadShippingPartyChanged(record.PayloadJson, out var fact))
        {
            await message.Services.GetRequiredService<ShopifyShippingPartyChangedApplier>().ApplyAsync(fact, cancellationToken);
        }
        else
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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Persistence;

namespace Modules.Wms.Infrastructure.Messaging;

/// <summary>
/// Inventario básico (spec §5.2): consume del outbox de plataforma los once eventos de §5.1 y delega cada uno en
/// <see cref="IInventoryEventApplier"/>. Por mensaje abre la transacción de la unidad de trabajo del <see cref="WmsDbContext"/>: el
/// efecto (reservas, asientos del ledger, fila de estado) y el inbox se confirman juntos, y un mensaje procesado nunca queda sin
/// marcar ni uno marcado sin su efecto.
/// <para>Payload malformado: procesado sin efecto y registrado, como Pipeline. Falla: se reintenta con la curva de Purchases (5 s,
/// 30 s, 5 minutos, y luego cada 5 minutos); desde el intento <see cref="ClaimedOutboxConsumer.ParkAfterAttempts"/> se registra en
/// Error y el documento queda <c>Parked</c>: sólo frena a ese documento (el orden por documento ya impide que pase uno posterior).
/// Singleton: el estado es del proceso.</para>
/// </summary>
internal sealed partial class WmsInventoryEventsProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<WmsInventoryEventsOptions> options,
    WmsMetrics metrics,
    ILogger<WmsInventoryEventsProcessor> logger)
{
    internal const string Consumer = "wms.inventory-events";

    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];

    internal ClaimedOutboxConsumer Claims { get; } = new(scopeFactory, Consumer, InventoryEventPayloads.EventNames, RetryDelays);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) has a malformed payload; it is marked as processed without effect.")]
    private static partial void LogMalformed(ILogger logger, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wms could not claim outbox message {MessageId}; it is retried on the next tick.")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wms failed to apply inventory event {MessageId} ({EventName}) on attempt {Attempt}; it is retried when its claim expires.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms keeps failing to apply inventory event {MessageId} ({EventName}) on attempt {Attempt}; its document is parked until the message passes.")]
    private static partial void LogAttemptFailedRepeatedly(ILogger logger, Exception exception, Guid messageId, string eventName, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms could not park the document of inventory event {MessageId}.")]
    private static partial void LogParkFailed(ILogger logger, Exception exception, Guid messageId);

    public Task<bool> ProcessBatchAsync(CancellationToken cancellationToken) =>
        Claims.ProcessBatchAsync(options.Value.StartAt, ProcessAsync, OnFailureAsync, cancellationToken);

    private async Task ProcessAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var unitOfWork = message.Services.GetRequiredService<IWmsUnitOfWork>();
        var record = message.Record;

        // El reclamo ya se commiteó solo; desde acá todo va en una transacción. Sin Commit, el Dispose la deshace.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (InventoryEventPayloads.TryParse(record.EventName, record.PayloadJson, out _))
        {
            var applier = message.Services.GetRequiredService<IInventoryEventApplier>();
            await applier.ApplyAsync(
                new InventoryMessage(record.Id, record.EventName, record.PayloadJson, record.OccurredAt, message.Attempts),
                cancellationToken);
        }
        else
        {
            LogMalformed(logger, record.Id, record.EventName);
        }

        await Claims.MarkProcessedAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // Sin intento (null) falló el reclamo mismo: no se contó nada y el mensaje vuelve en el tick siguiente.
    private async Task OnFailureAsync(Exception exception, OutboxRow record, int? attempts, CancellationToken cancellationToken)
    {
        if (attempts is not { } attempt)
        {
            LogClaimFailed(logger, exception, record.Id);
            return;
        }

        if (attempt < ClaimedOutboxConsumer.ParkAfterAttempts)
        {
            LogAttemptFailed(logger, exception, record.Id, record.EventName, attempt);
            return;
        }

        LogAttemptFailedRepeatedly(logger, exception, record.Id, record.EventName, attempt);
        metrics.RecordDocumentParked();
        await ParkAsync(record, cancellationToken);
    }

    /// <summary>
    /// Marca el documento <c>Parked</c> sólo si todavía no tenía estado (<c>None</c> o sin fila): sobre un estado real (por ejemplo
    /// <c>TakenOutByPrepare</c>) la marca lo borraría y la máquina perdería dónde estaba. El orden por documento ya frena todo lo
    /// posterior; el log de error y la métrica avisan igual.
    /// </summary>
    private async Task ParkAsync(OutboxRow record, CancellationToken cancellationToken)
    {
        if (!InventoryEventPayloads.TryParse(record.EventName, record.PayloadJson, out var parsed))
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<BuildingBlocks.Application.IClock>().UtcNow;
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO wms.document_stock_states (tenant_id, document_type, document_id, last_version, status, skipped_line_ids, updated_at)
                VALUES ({parsed.TenantId}, {parsed.DocumentType}, {parsed.DocumentId}, 0, {nameof(DocumentStockStatus.Parked)}, '[]'::jsonb, {now})
                ON CONFLICT (tenant_id, document_type, document_id) DO UPDATE
                    SET status = {nameof(DocumentStockStatus.Parked)}, updated_at = {now}
                    WHERE document_stock_states.status = {nameof(DocumentStockStatus.None)}
                """,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogParkFailed(logger, exception, record.Id);
        }
    }
}

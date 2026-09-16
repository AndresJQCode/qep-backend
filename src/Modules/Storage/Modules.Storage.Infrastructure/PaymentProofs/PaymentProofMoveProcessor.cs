using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.PaymentProofs;

internal interface IPaymentProofMoveProcessor
{
    /// <returns>Cuántos mensajes quedaron procesados en este lote.</returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Spec 2026-09-16, D9, paso 4. Consume del outbox de plataforma el evento que Quotations escribe al
// adjuntar comprobantes, con el mismo esqueleto que OrphanUserCleanupWorker: anti-join contra el inbox
// propio con clave (consumidor, id de mensaje), y efecto e inbox en el mismo SaveChanges.
//
// El orden importa. Primero se borra el temporal —que ya tiene su copia pública— y recién después se
// guarda el movimiento con el inbox. Borrar una clave que ya no existe no falla, así que el paso se
// puede repetir: si el borrado falla no se guarda nada y el mensaje vuelve; si falla el guardado, el
// tick siguiente repite el borrado (sin efecto) y guarda. Al revés, un borrado fallido dejaría un
// huérfano en staging/ para siempre: el inbox ya estaría marcado y el barrido sólo mira comprobantes
// sin mover.
//
// Los errores se separan en dos. Los deterministas no se arreglan reintentando, así que no se
// reintentan: un payload mal formado se registra y se marca en el inbox, y una entrada inválida (sin
// clave pública, o que MoveToPublic rechaza) se salta mientras las válidas del mismo mensaje se mueven.
// Los transitorios —R2, la base, un timeout que llega como cancelación sin que nadie apague el host—
// descartan el mensaje entero, que vuelve en el tick siguiente.
internal sealed partial class PaymentProofMoveProcessor(
    StorageDbContext dbContext,
    IObjectStorage objectStorage,
    IClock clock,
    ILogger<PaymentProofMoveProcessor> logger) : IPaymentProofMoveProcessor
{
    internal const string Consumer = "storage.payment-proof-move";
    internal const string AttachedEvent = "quotations.order.payment-proofs-attached.v1";
    private const int BatchSize = 20;

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Payment proof move failed for outbox message {MessageId}; it will be retried.")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} has a malformed payment proof payload; it is marked as processed without moving anything.")]
    private static partial void LogMalformedMessage(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} lists payment proof file {FileId} without a public storage key; the entry is skipped.")]
    private static partial void LogEntryWithoutPublicKey(ILogger logger, Guid messageId, Guid fileId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} cannot move payment proof file {FileId} ({Code}); the entry is skipped.")]
    private static partial void LogEntryRejected(ILogger logger, Guid messageId, Guid fileId, string code);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == AttachedEvent)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var record in pending)
        {
            try
            {
                await MoveAsync(record, cancellationToken);
                processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un error transitorio (un borrado en R2, un timeout, un conflicto con otra réplica) no
                // frena a los demás mensajes: se descarta lo rastreado y, sin inbox, vuelve en el tick
                // siguiente.
                LogMessageFailed(logger, exception, record.Id);
                dbContext.ChangeTracker.Clear();
            }
        }

        return processed;
    }

    private async Task MoveAsync(StorageOutboxMessage record, CancellationToken cancellationToken)
    {
        if (!AttachedPayload.TryParse(record.PayloadJson, out var payload, out var parseError))
        {
            LogMalformedMessage(logger, parseError, record.Id);
            await MarkProcessedAsync(record, cancellationToken);
            return;
        }

        foreach (var proof in payload.Proofs)
        {
            if (string.IsNullOrWhiteSpace(proof.PublicStorageKey))
            {
                LogEntryWithoutPublicKey(logger, record.Id, proof.FileId);
                continue;
            }

            // El tenant va en la consulta, no en memoria: ningún archivo de otro tenant se carga.
            var fileId = new FileResourceId(proof.FileId);
            var resource = await dbContext.FileResources
                .FirstOrDefaultAsync(
                    file => file.Id == fileId && file.TenantId == payload.TenantId, cancellationToken);
            if (!IsWaitingToMove(resource))
            {
                continue;
            }

            // MoveToPublic sólo cambia la entidad en memoria, y se llama antes del borrado para que una
            // entrada que el dominio rechaza no pierda su temporal. En la base el orden de D9 no cambia:
            // nada se guarda hasta el SaveChanges de MarkProcessedAsync, que va después de todos los
            // borrados, y si un borrado falla se descarta lo rastreado. occurredAt es el del mensaje,
            // cuando se guardó el pedido: desde ahí el comprobante es público, y reintentar no mueve la
            // fecha.
            try
            {
                resource.MoveToPublic(proof.PublicStorageKey, record.OccurredAt);
            }
            catch (StorageDomainException exception)
            {
                LogEntryRejected(logger, record.Id, proof.FileId, exception.Code);
                continue;
            }

            await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
        }

        await MarkProcessedAsync(record, cancellationToken);
    }

    private async Task MarkProcessedAsync(StorageOutboxMessage record, CancellationToken cancellationToken)
    {
        dbContext.Inbox.Add(new StorageInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Se salta, y el mensaje se marca igual, lo que nunca va a poder moverse: un archivo que no existe
    // o es de otro tenant (la consulta no lo trae), un comprobante User (D13), uno que ya no está
    // Available o uno ya movido. Un mensaje así reintentado cada 3 s no arreglaría nada. Esa última
    // condición cubre también un mismo archivo que el evento lista dos veces con claves distintas: la
    // primera lo mueve y la segunda lo encuentra ya movido en el ChangeTracker, así que no llega a
    // MoveToPublic ni envenena el mensaje.
    private static bool IsWaitingToMove([NotNullWhen(true)] FileResource? resource) =>
        resource is not null
        && resource.OwnerType is FileOwnerType.PaymentProof
        && resource.Status is FileResourceStatus.Available
        && resource.PublicStorageKey is null;

    private sealed record AttachedPayload(Guid TenantId, IReadOnlyList<AttachedProof> Proofs)
    {
        // Por nombre, igual que los demás consumidores del outbox: el payload lo escribe
        // OrderPaymentProofEventPublisher, en Quotations.
        // Un payload que no se puede leer es determinista: reintentarlo da el mismo error. Las cuatro
        // excepciones son las que lanzan JsonDocument.Parse y los GetProperty, GetGuid, GetString y
        // EnumerateArray de JsonElement con un campo ausente, de otro tipo o con un Guid inválido.
        public static bool TryParse(
            string payloadJson,
            [NotNullWhen(true)] out AttachedPayload? payload,
            [NotNullWhen(false)] out Exception? error)
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                payload = new AttachedPayload(
                    root.GetProperty("tenantId").GetGuid(),
                    root.GetProperty("proofs")
                        .EnumerateArray()
                        .Select(proof => new AttachedProof(
                            proof.GetProperty("fileId").GetGuid(),
                            proof.GetProperty("publicStorageKey").GetString()))
                        .ToArray());
                error = null;
                return true;
            }
            catch (Exception exception) when (exception is JsonException
                or KeyNotFoundException
                or FormatException
                or InvalidOperationException)
            {
                payload = null;
                error = exception;
                return false;
            }
        }
    }

    private sealed record AttachedProof(Guid FileId, string? PublicStorageKey);
}

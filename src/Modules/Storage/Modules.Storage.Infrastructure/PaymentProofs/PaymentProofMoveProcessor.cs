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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un mensaje que falla (un borrado en R2, un conflicto con otra réplica) no frena a los
                // demás: se descarta lo rastreado y, sin inbox, vuelve en el tick siguiente.
                LogMessageFailed(logger, exception, record.Id);
                dbContext.ChangeTracker.Clear();
            }
        }

        return processed;
    }

    private async Task MoveAsync(StorageOutboxMessage record, CancellationToken cancellationToken)
    {
        var payload = AttachedPayload.Parse(record.PayloadJson);
        foreach (var proof in payload.Proofs)
        {
            var fileId = new FileResourceId(proof.FileId);
            var resource = await dbContext.FileResources
                .FirstOrDefaultAsync(file => file.Id == fileId, cancellationToken);
            if (!IsWaitingToMove(resource, payload.TenantId))
            {
                continue;
            }

            await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
            // occurredAt es el del mensaje, cuando se guardó el pedido: desde ahí el comprobante es
            // público, y reintentar no mueve la fecha.
            resource.MoveToPublic(proof.PublicStorageKey, record.OccurredAt);
        }

        dbContext.Inbox.Add(new StorageInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Se salta, y el mensaje se marca igual, lo que nunca va a poder moverse: un archivo que no existe,
    // de otro tenant, un comprobante User (D13), uno que ya no está Available o uno ya movido. Un
    // mensaje así reintentado cada 3 s no arreglaría nada. Esa última condición cubre también un mismo
    // archivo que el evento lista dos veces con claves distintas: la primera lo mueve y la segunda lo
    // encuentra ya movido en el ChangeTracker, así que no llega a MoveToPublic ni envenena el mensaje.
    private static bool IsWaitingToMove([NotNullWhen(true)] FileResource? resource, Guid tenantId) =>
        resource is not null
        && resource.TenantId == tenantId
        && resource.OwnerType is FileOwnerType.PaymentProof
        && resource.Status is FileResourceStatus.Available
        && resource.PublicStorageKey is null;

    private sealed record AttachedPayload(Guid TenantId, IReadOnlyList<AttachedProof> Proofs)
    {
        // Por nombre, igual que los demás consumidores del outbox: el payload lo escribe
        // OrderPaymentProofEventPublisher, en Quotations.
        public static AttachedPayload Parse(string payloadJson)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            return new AttachedPayload(
                root.GetProperty("tenantId").GetGuid(),
                root.GetProperty("proofs")
                    .EnumerateArray()
                    .Select(proof => new AttachedProof(
                        proof.GetProperty("fileId").GetGuid(),
                        proof.GetProperty("publicStorageKey").GetString() ?? string.Empty))
                    .ToArray());
        }
    }

    private sealed record AttachedProof(Guid FileId, string PublicStorageKey);
}

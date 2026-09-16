using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.PaymentProofs;

internal interface IPaymentProofDetachProcessor
{
    /// <returns>Cuántos mensajes quedaron procesados en este lote.</returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Spec 2026-09-16, D19. Consume del outbox de plataforma el evento que Quotations escribe al reemplazar o
// quitar comprobantes, con el mismo esqueleto que PaymentProofMoveProcessor: anti-join contra el inbox
// propio, y purga y auditoría en el mismo SaveChanges.
//
// Mismo orden que D9 y por la misma razón: primero se borran los objetos y después se guarda. Borrar una
// clave que ya no existe no falla, así que si el guardado falla el tick siguiente repite los borrados
// (sin efecto) y guarda. Al revés, un borrado fallido con el inbox ya marcado dejaría expuesto para
// siempre el comprobante que D19 quiere borrar.
//
// Un mensaje puede soltar varios archivos. Cada archivo se guarda antes de pasar al siguiente, y el
// último junto con el inbox: si el borrado del segundo falla, el primero ya quedó Purged —no Available
// sin objeto, que se podría descargar o adjuntar— y el reintento lo salta porque ya no está Available.
// Un mensaje de un solo archivo, el caso de siempre, queda en un solo SaveChanges.
//
// Los errores se separan igual que en el movimiento. Los deterministas no se reintentan: un payload mal
// formado se registra y se marca en el inbox, y una entrada que el dominio rechaza se salta. Los
// transitorios —R2, la base, un timeout que llega como cancelación sin que nadie apague el host—
// descartan lo pendiente del mensaje, que vuelve en el tick siguiente.
internal sealed partial class PaymentProofDetachProcessor(
    StorageDbContext dbContext,
    IObjectStorage objectStorage,
    IPublicObjectStorage publicObjectStorage,
    IEnumerable<IFileReferenceProbe> probes,
    IStorageAuditPublisher auditPublisher,
    IClock clock,
    ILogger<PaymentProofDetachProcessor> logger) : IPaymentProofDetachProcessor
{
    internal const string Consumer = "storage.payment-proof-detach";
    internal const string DetachedEvent = "quotations.order.payment-proofs-detached.v1";
    internal const string Reason = "payment_proof_detached";
    private const int BatchSize = 20;

    private readonly List<IFileReferenceProbe> _probes = probes.ToList();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Payment proof detach failed for outbox message {MessageId}; it will be retried.")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} has a malformed payment proof detach payload; it is marked as processed without purging anything.")]
    private static partial void LogMalformedMessage(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} cannot purge payment proof file {FileId} ({Code}); the entry is skipped.")]
    private static partial void LogEntryRejected(ILogger logger, Guid messageId, Guid fileId, string code);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No file reference probe is registered: {Count} detached payment proof messages wait until one is.")]
    private static partial void LogNoProbes(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Detached payment proof {FileId} kept: still referenced by {Source}.")]
    private static partial void LogProofRetained(ILogger logger, Guid fileId, string source);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Detached payment proof {FileId} purged.")]
    private static partial void LogProofPurged(ILogger logger, Guid fileId);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == DetachedEvent)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        // Sin sondas no hay forma de saber si otro pedido usa el archivo: purgar sería borrar
        // comprobantes de pedidos. Se falla cerrado, igual que StagingCleanupProcessor, pero sin marcar
        // el inbox: los mensajes esperan y se aplican cuando haya sondas. Es un Warning y sólo cuando hay
        // mensajes pendientes, no un Error en cada tick.
        if (_probes.Count == 0)
        {
            LogNoProbes(logger, pending.Count);
            return 0;
        }

        var processed = 0;
        foreach (var record in pending)
        {
            try
            {
                await DetachAsync(record, cancellationToken);
                processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un error transitorio no frena a los demás mensajes: se descarta lo rastreado de la
                // entrada que falló (lo de las anteriores ya se guardó) y, sin inbox, el mensaje vuelve
                // en el tick siguiente.
                LogMessageFailed(logger, exception, record.Id);
                dbContext.ChangeTracker.Clear();
            }
        }

        return processed;
    }

    private async Task DetachAsync(StorageOutboxMessage record, CancellationToken cancellationToken)
    {
        if (!DetachedPayload.TryParse(record.PayloadJson, out var payload, out var parseError))
        {
            LogMalformedMessage(logger, parseError, record.Id);
            await MarkProcessedAsync(record, cancellationToken);
            return;
        }

        // La fecha es la del borrado, como en el barrido, no la del mensaje.
        var now = clock.UtcNow;
        for (var index = 0; index < payload.Proofs.Count; index++)
        {
            await DetachEntryAsync(record, payload.TenantId, payload.Proofs[index], now, cancellationToken);

            var isLast = index == payload.Proofs.Count - 1;
            if (!isLast && dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        await MarkProcessedAsync(record, cancellationToken);
    }

    private async Task DetachEntryAsync(
        StorageOutboxMessage record,
        Guid tenantId,
        DetachedProof proof,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // El tenant va en la consulta, no en memoria: ningún archivo de otro tenant se carga.
        var fileId = new FileResourceId(proof.FileId);
        var resource = await dbContext.FileResources
            .FirstOrDefaultAsync(file => file.Id == fileId && file.TenantId == tenantId, cancellationToken);
        if (resource is null)
        {
            return;
        }

        if (resource.OwnerType is not FileOwnerType.PaymentProof)
        {
            await DeleteAttachmentCopyAsync(tenantId, proof.PublicStorageKey, now, cancellationToken);
            return;
        }

        // Ya purgado (por otro mensaje, o por este mismo en un intento anterior), borrado o en cuarentena:
        // no hay nada que borrar, y reintentar no lo cambiaría.
        if (resource.Status is not FileResourceStatus.Available)
        {
            return;
        }

        var retainedBy = await FindRetainingSourceAsync(resource.Id.Value, cancellationToken);
        if (retainedBy is not null)
        {
            // Otro comprobante todavía lo usa. Una copia que quede sin dueño la recoge la
            // reconciliación de payment-proofs/ (D12).
            LogProofRetained(logger, resource.Id.Value, retainedBy);
            return;
        }

        // La purga sólo cambia la entidad en memoria, y va antes de los borrados para que una entrada
        // que el dominio rechaza no pierda su objeto. En la base el orden de D9 no cambia: nada se guarda
        // hasta después de los borrados, y si uno falla se descarta lo rastreado.
        try
        {
            resource.PurgeDetachedPaymentProof(now);
        }
        catch (StorageDomainException exception)
        {
            LogEntryRejected(logger, record.Id, proof.FileId, exception.Code);
            return;
        }

        if (resource.PublicStorageKey is { } movedKey)
        {
            // Movido: la copia pública es su única copia (D9).
            await publicObjectStorage.DeleteAsync(movedKey, cancellationToken);
        }
        else
        {
            // Sin mover: el temporal sigue en staging/ y, si el adjunto alcanzó a copiarse, su copia
            // pública también existe aunque el movimiento nunca la registró. Es la carrera de D19: el
            // pedido lo soltó antes de que PaymentProofMoveWorker procesara el adjunto, que después
            // lo salta porque ya no está Available.
            await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(proof.PublicStorageKey))
            {
                await publicObjectStorage.DeleteAsync(proof.PublicStorageKey, cancellationToken);
            }
        }

        auditPublisher.PublishSystem(
            resource.TenantId,
            "storage.file.purged",
            "file",
            resource.Id.ToString(),
            Reason,
            now);
        LogProofPurged(logger, resource.Id.Value);
    }

    // D13 y D19: de un comprobante User sólo se borra la copia pública de ese adjunto.
    // PublicPaymentProofPublisher le da a cada adjunto una clave aleatoria propia, así que ningún otro
    // comprobante la usa; el original privado no se toca.
    private async Task DeleteAttachmentCopyAsync(
        Guid tenantId, string? copyKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(copyKey))
        {
            return;
        }

        await publicObjectStorage.DeleteAsync(copyKey, cancellationToken);
        auditPublisher.PublishSystem(
            tenantId,
            "storage.public_object.purged",
            "public_object",
            copyKey,
            Reason,
            now);
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

    // Secuencial y cortando en la primera que retiene, igual que StagingCleanupProcessor.
    private async Task<string?> FindRetainingSourceAsync(Guid fileId, CancellationToken cancellationToken)
    {
        foreach (var probe in _probes)
        {
            if (await probe.HasReferencesAsync(fileId, cancellationToken))
            {
                return probe.Source;
            }
        }

        return null;
    }

    private sealed record DetachedPayload(Guid TenantId, IReadOnlyList<DetachedProof> Proofs)
    {
        // Por nombre, igual que en PaymentProofMoveProcessor: el payload lo escribe
        // OrderPaymentProofEventPublisher, en Quotations. publicStorageKey viene null cuando el
        // comprobante no tenía copia.
        // Un payload que no se puede leer es determinista: reintentarlo da el mismo error. Las cuatro
        // excepciones son las que lanzan JsonDocument.Parse y los GetProperty, GetGuid, GetString y
        // EnumerateArray de JsonElement con un campo ausente, de otro tipo o con un Guid inválido.
        public static bool TryParse(
            string payloadJson,
            [NotNullWhen(true)] out DetachedPayload? payload,
            [NotNullWhen(false)] out Exception? error)
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                payload = new DetachedPayload(
                    root.GetProperty("tenantId").GetGuid(),
                    root.GetProperty("proofs")
                        .EnumerateArray()
                        .Select(proof => new DetachedProof(
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

    private sealed record DetachedProof(Guid FileId, string? PublicStorageKey);
}

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.ProductImages;

internal interface IProductImageReleaseProcessor
{
    /// <returns>Cuántos mensajes quedaron procesados en este lote.</returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Spec 2026-11-08. Consume del outbox de plataforma el evento que Catalog escribe cuando una foto de
// producto deja de usarse, con el mismo esqueleto que PaymentProofDetachProcessor: anti-join contra el
// inbox propio, y purga y auditoría en el mismo SaveChanges.
//
// Mismo orden que el retiro de comprobantes y por la misma razón: primero se borran los objetos y después
// se guarda. Borrar una clave que ya no existe no falla, así que si el guardado falla el tick siguiente
// repite los borrados (sin efecto) y guarda. Al revés, un borrado fallido con el inbox ya marcado dejaría
// la foto en el bucket para siempre.
//
// Un mensaje puede soltar varias fotos. Cada foto se guarda antes de pasar a la siguiente, y la última junto
// con el inbox: si el borrado de la segunda falla, la primera ya quedó Purged —no Available sin objeto— y el
// reintento la salta porque ya no está Available.
//
// Los errores se separan igual que en el retiro. Los deterministas no se reintentan: un payload mal formado
// se registra y se marca en el inbox, y una entrada que el dominio rechaza se salta. Los transitorios —R2,
// la base, un timeout que llega como cancelación sin que nadie apague el host— descartan lo pendiente del
// mensaje, que vuelve en el tick siguiente.
internal sealed partial class ProductImageReleaseProcessor(
    StorageDbContext dbContext,
    IObjectStorage objectStorage,
    FilePublication filePublication,
    IEnumerable<IFileReferenceProbe> probes,
    IStorageAuditPublisher auditPublisher,
    IClock clock,
    ILogger<ProductImageReleaseProcessor> logger) : IProductImageReleaseProcessor
{
    internal const string Consumer = "storage.product-image-release";
    internal const string Reason = "product_image_released";
    private const int BatchSize = 20;

    private readonly List<IFileReferenceProbe> _probes = probes.ToList();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Product image release failed for outbox message {MessageId}; it will be retried.")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} has a malformed product image release payload; it is marked as processed without purging anything.")]
    private static partial void LogMalformedMessage(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} cannot purge product image {FileId} ({Code}); the entry is skipped.")]
    private static partial void LogEntryRejected(ILogger logger, Guid messageId, Guid fileId, string code);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} releases file {FileId}, which is a {OwnerType} file and not a product image; it is skipped.")]
    private static partial void LogNotAProductImage(ILogger logger, Guid messageId, Guid fileId, FileOwnerType ownerType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No file reference probe is registered: {Count} released product image messages wait until one is.")]
    private static partial void LogNoProbes(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Released product image {FileId} kept: still referenced by {Source}.")]
    private static partial void LogImageRetained(ILogger logger, Guid fileId, string source);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Released product image {FileId} purged.")]
    private static partial void LogImagePurged(ILogger logger, Guid fileId);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == StorageSubscribedEvents.ProductImagesReleased)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        // Sin sondas no hay forma de saber si un producto todavía usa la foto: purgar sería borrar fotos de
        // productos vivos. Se falla cerrado, sin marcar el inbox: los mensajes esperan y se aplican cuando
        // haya sondas. Es un Warning y sólo cuando hay mensajes pendientes, no un Error en cada tick.
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
                await ReleaseAsync(record, cancellationToken);
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

    private async Task ReleaseAsync(StorageOutboxMessage record, CancellationToken cancellationToken)
    {
        if (!ReleasedPayload.TryParse(record.PayloadJson, out var payload, out var parseError))
        {
            LogMalformedMessage(logger, parseError, record.Id);
            await MarkProcessedAsync(record, cancellationToken);
            return;
        }

        // La fecha es la de la purga, no la del mensaje.
        var now = clock.UtcNow;
        for (var index = 0; index < payload.FileIds.Count; index++)
        {
            await ReleaseImageAsync(record, payload.TenantId, payload.FileIds[index], now, cancellationToken);

            var isLast = index == payload.FileIds.Count - 1;
            if (!isLast && dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        await MarkProcessedAsync(record, cancellationToken);
    }

    private async Task ReleaseImageAsync(
        StorageOutboxMessage record,
        Guid tenantId,
        Guid rawFileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // El tenant va en la consulta, no en memoria: ningún archivo de otro tenant se carga.
        var fileId = new FileResourceId(rawFileId);
        var resource = await dbContext.FileResources
            .Include(file => file.Variants)
            .FirstOrDefaultAsync(file => file.Id == fileId && file.TenantId == tenantId, cancellationToken);
        if (resource is null)
        {
            return;
        }

        // Un id de otro tipo de archivo en el evento nunca se purga: el evento sólo habla de fotos de producto.
        if (resource.OwnerType is not FileOwnerType.Product)
        {
            LogNotAProductImage(logger, record.Id, rawFileId, resource.OwnerType);
            return;
        }

        // Ya purgada (por este mismo mensaje en un intento anterior), borrada o en cuarentena: no se toca.
        if (resource.Status is not FileResourceStatus.Available)
        {
            return;
        }

        var retainedBy = await FindRetainingSourceAsync(rawFileId, cancellationToken);
        if (retainedBy is not null)
        {
            LogImageRetained(logger, rawFileId, retainedBy);
            return;
        }

        // La purga sólo cambia la entidad en memoria, y va antes de los borrados para que una entrada que el
        // dominio rechaza no pierda su objeto. En la base el orden no cambia: nada se guarda hasta después
        // de los borrados, y si uno falla se descarta lo rastreado.
        try
        {
            resource.PurgeReleasedProductImage(now);
        }
        catch (StorageDomainException exception)
        {
            LogEntryRejected(logger, record.Id, rawFileId, exception.Code);
            return;
        }

        await objectStorage.DeleteAsync(resource.StorageKey, cancellationToken);
        foreach (var variant in resource.Variants)
        {
            await objectStorage.DeleteAsync(variant.StorageKey, cancellationToken);
        }

        // Publicada: las copias públicas (original y variantes) las retira FilePublication, el único dueño del
        // esquema de claves públicas. Deja la fila sin PublicStorageKey, a diferencia de la purga de
        // comprobantes: así una foto purgada no conserva una URL pública que ya no sirve. Unpublish no tiene
        // precondiciones, y como la purga del dominio ya corrió arriba, ningún borrado ocurre si ésta se rechaza.
        await filePublication.UnpublishAsync(resource, cancellationToken);

        auditPublisher.PublishSystem(
            resource.TenantId,
            "storage.file.purged",
            "file",
            resource.Id.ToString(),
            Reason,
            now);
        LogImagePurged(logger, rawFileId);
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

    // Secuencial y cortando en la primera que retiene, igual que el retiro de comprobantes.
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

    private sealed record ReleasedPayload(Guid TenantId, IReadOnlyList<Guid> FileIds)
    {
        // Por nombre: el payload lo escribe Catalog (CatalogIntegrationEvents.ProductImagesReleased).
        // Un payload que no se puede leer es determinista: reintentarlo da el mismo error. Las cuatro
        // excepciones son las que lanzan JsonDocument.Parse y los GetProperty, GetGuid y EnumerateArray de
        // JsonElement con un campo ausente, de otro tipo o con un Guid inválido.
        public static bool TryParse(
            string payloadJson,
            [NotNullWhen(true)] out ReleasedPayload? payload,
            [NotNullWhen(false)] out Exception? error)
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                payload = new ReleasedPayload(
                    root.GetProperty("tenantId").GetGuid(),
                    root.GetProperty("fileIds")
                        .EnumerateArray()
                        .Select(fileId => fileId.GetGuid())
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
}

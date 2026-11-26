using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.TemplateImages;

internal interface ITemplateHeaderImageReleaseProcessor
{
    /// <returns>Cuántos mensajes quedaron procesados en este lote.</returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Spec 2026-11-17 («No orphaned images»), plan Task 9. Consume del outbox de plataforma el aviso que Messaging escribe cuando la imagen fija
// del encabezado de una plantilla se queda sin fila. Mismo esqueleto que ProductImageReleaseProcessor, sin sondas: el archivo es de dueño de
// sistema (FileOwnerType.MessageTemplate) y sólo su plantilla lo usa, así que el aviso basta para soltarlo.
//
// El orden es el de las fotos de producto: primero se retiran las copias públicas y después se guarda (borrado lógico, auditoría e inbox en
// un solo SaveChanges). Borrar una clave pública que ya no existe no falla, así que si el guardado falla el tick siguiente repite el retiro
// (sin efecto) y guarda. Al revés, un retiro fallido con el inbox ya marcado dejaría la imagen pública para siempre.
//
// La auditoría es del sistema: el aviso no trae a nadie, y la liberación sincrónica (el adaptador de Bootstrapper) es la que audita a quien
// llamó. Los errores se separan como en las fotos: un payload mal formado se registra y se marca; un archivo que no existe, de otro dueño o
// ya retirado se marca sin tocar nada; lo transitorio (R2, la base, un timeout que llega como cancelación sin que nadie apague el host)
// descarta lo pendiente del mensaje, no escribe inbox, y el mensaje vuelve en el tick siguiente.
internal sealed partial class TemplateHeaderImageReleaseProcessor(
    StorageDbContext dbContext,
    FilePublication filePublication,
    IStorageAuditPublisher auditPublisher,
    IClock clock,
    ILogger<TemplateHeaderImageReleaseProcessor> logger) : ITemplateHeaderImageReleaseProcessor
{
    internal const string Consumer = "storage.template-header-image-release";
    internal const string Reason = "template_header_image_released";
    internal const int BatchSize = 20;

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Template header image release failed for outbox message {MessageId}; it will be retried.")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} has a malformed template header image release payload; it is marked as processed without releasing anything.")]
    private static partial void LogMalformedMessage(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} releases file {FileId}, which is a {OwnerType} file and not a template header image; it is skipped.")]
    private static partial void LogNotATemplateImage(ILogger logger, Guid messageId, Guid fileId, FileOwnerType ownerType);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Released template header image {FileId} unpublished and deleted.")]
    private static partial void LogImageReleased(ILogger logger, Guid fileId);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        // Anti-join contra el inbox sin reclamar: dos réplicas pueden tomar el mismo mensaje; las dos retiran (idempotente) y la que pierde
        // choca con la clave (consumer, message_id) del inbox al guardar, descarta y en el tick siguiente ya no lo ve.
        var pending = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == StorageSubscribedEvents.TemplateHeaderImageReleased)
            .Where(record => !dbContext.Inbox.Any(entry => entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

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
                // Un error transitorio no frena a los demás mensajes: se descarta lo rastreado de éste y, sin inbox, vuelve en el tick siguiente.
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

        await ReleaseImageAsync(record, payload.TenantId, payload.FileId, cancellationToken);
        await MarkProcessedAsync(record, cancellationToken);
    }

    private async Task ReleaseImageAsync(StorageOutboxMessage record, Guid tenantId, Guid rawFileId, CancellationToken cancellationToken)
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

        // El aviso sólo habla de imágenes de encabezado: un id de otro tipo de archivo nunca se retira.
        if (resource.OwnerType is not FileOwnerType.MessageTemplate)
        {
            LogNotATemplateImage(logger, record.Id, rawFileId, resource.OwnerType);
            return;
        }

        // Ya retirada (otro aviso del mismo archivo, o la liberación sincrónica que sí alcanzó a correr): nada que hacer.
        if (resource.Status is FileResourceStatus.Deleted or FileResourceStatus.Purged)
        {
            return;
        }

        var now = clock.UtcNow;
        await filePublication.RemovePublicCopiesAsync(resource, cancellationToken);
        resource.SoftDelete(now);
        auditPublisher.PublishSystem(resource.TenantId, "storage.file.deleted", "file", resource.Id.ToString(), Reason, now);
        LogImageReleased(logger, rawFileId);
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

    private sealed record ReleasedPayload(Guid TenantId, Guid FileId)
    {
        // Por nombre: el payload lo escribe Messaging (TemplateHeaderImageReleasePublisher). Un payload que no se puede leer es
        // determinista: reintentarlo da el mismo error. Las excepciones son las de JsonDocument.Parse y los GetProperty y GetGuid de
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
                payload = new ReleasedPayload(root.GetProperty("tenantId").GetGuid(), root.GetProperty("fileId").GetGuid());
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

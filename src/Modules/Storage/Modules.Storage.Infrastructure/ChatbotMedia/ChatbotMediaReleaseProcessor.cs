using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;
using Modules.Storage.Infrastructure.UnreferencedFiles;

namespace Modules.Storage.Infrastructure.ChatbotMedia;

internal interface IChatbotMediaReleaseProcessor
{
    /// <returns>Cuántos mensajes quedaron procesados en este lote.</returns>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Spec 2026-11-18 («Lifecycle»), plan Task 12. Consume del outbox de plataforma el aviso que Automations escribe cuando se borra un chatbot
// con archivos en sus bloques «Mensaje». Mismo esqueleto que TemplateHeaderImageReleaseProcessor, con una diferencia: aquí el aviso no basta,
// porque un archivo de chatbot lo pueden usar varios chatbots a la vez (una copia, una plantilla guardada) y la bandeja lo sigue mostrando.
// Por cada id se pregunta a Automations y Messaging (IChatbotMediaReferences, un adaptador de Bootstrapper) cuáles siguen en uso; los demás
// los suelta UnreferencedFileReleaser, el mismo de los barridos diarios, que vuelve a preguntar bajo el candado de la fila.
//
// Falla cerrado: si la consulta de uso o la liberación de un archivo falla (R2, la base), no se escribe el inbox y el mensaje vuelve en el
// tick siguiente; lo ya soltado no se repite (queda Deleted). Un payload mal formado se registra y se marca. Un archivo que no existe, ya
// se soltó, es de otro dueño o de otro tenant se salta sin tocarlo (el reclamo filtra por dueño y tenant).
internal sealed partial class ChatbotMediaReleaseProcessor(
    StorageDbContext dbContext,
    IChatbotMediaReferences references,
    UnreferencedFileReleaser releaser,
    IClock clock,
    ILogger<ChatbotMediaReleaseProcessor> logger) : IChatbotMediaReleaseProcessor
{
    internal const string Consumer = "storage.chatbot-media-release";
    internal const string Reason = "chatbot_media_released";
    internal const int BatchSize = 20;

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Chatbot media release failed for outbox message {MessageId}; it will be retried.")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} has a malformed chatbot media release payload; it is marked as processed without releasing anything.")]
    private static partial void LogMalformedMessage(ILogger logger, Exception exception, Guid messageId);

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        // Anti-join contra el inbox sin reclamar: dos réplicas pueden tomar el mismo mensaje; las dos preguntan y sueltan (el reclamo con
        // SKIP LOCKED deja a una sola) y la que pierde choca con la clave (consumer, message_id) del inbox al guardar, descarta y en el
        // tick siguiente ya no lo ve.
        var pending = await dbContext.Outbox
            .AsNoTracking()
            .Where(record => record.EventName == StorageSubscribedEvents.ChatbotMediaReleased)
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

        if (payload.FileIds.Count > 0)
        {
            var referenced = await references.FindReferencedAsync(payload.TenantId, payload.FileIds, cancellationToken);
            foreach (var fileId in payload.FileIds.Where(fileId => !referenced.Contains(fileId)))
            {
                // Un archivo que alguien volvió a usar entre la pregunta de arriba y el reclamo se pregunta otra vez, bajo el candado.
                await releaser.ReleaseAsync(
                    FileOwnerType.ChatbotMedia,
                    payload.TenantId,
                    new FileResourceId(fileId),
                    Reason,
                    async () => (await references.FindReferencedAsync(payload.TenantId, [fileId], cancellationToken)).Contains(fileId),
                    clock.UtcNow,
                    cancellationToken);
            }
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

    private sealed record ReleasedPayload(Guid TenantId, IReadOnlyList<Guid> FileIds)
    {
        // Por nombre: el payload lo escribe Automations (ChatbotMediaReleasePublisher). Un payload que no se puede leer es determinista:
        // reintentarlo da el mismo error. Las excepciones son las de JsonDocument.Parse y los GetProperty, EnumerateArray y GetGuid de
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
                var fileIds = root.GetProperty("fileIds").EnumerateArray().Select(element => element.GetGuid()).Distinct().ToList();
                payload = new ReleasedPayload(root.GetProperty("tenantId").GetGuid(), fileIds);
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

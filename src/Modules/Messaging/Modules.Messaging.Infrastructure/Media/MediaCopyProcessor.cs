using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.Infrastructure.Media;

internal sealed record PendingMedia(Guid MessageId, Guid TenantId, Guid ConnectionId, string MetaMediaId, DateTimeOffset OccurredAt, int Attempts);

/// <summary>§8.6, «Copia»: por cada medio reclamado, token → GET /{id} → stream a R2 con SHA-256 al
/// pasar → fila. Nunca registra el token ni la URL firmada. Un fallo deja last_error y el reintento ya
/// quedó programado por el reclamo; rendirse corre next_attempt_at a <see cref="GiveUpAt"/>.</summary>
internal sealed partial class MediaCopyProcessor(
    MessagingDbContext dbContext,
    IMessagingConnectionDirectory connections,
    IWhatsAppCloudClient meta,
    IMessagingMediaStore store,
    IClock clock,
    ILogger<MediaCopyProcessor> logger)
{
    /// <summary>§8.6: el tope de WhatsApp para un documento. Más que eso no se intenta.</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>El ancho de <c>message_media.meta_media_id</c>.</summary>
    public const int MetaMediaIdMaxLength = 64;

    public const int LastErrorMaxLength = 256;

    /// <summary>§8.6: Meta guarda el medio 7 días; pasado eso no hay qué bajar.</summary>
    public static readonly TimeSpan MetaRetention = TimeSpan.FromDays(7);

    /// <summary>Rendirse = que el reclamo no la vuelva a tomar: el motivo queda en last_error.</summary>
    public static readonly DateTimeOffset GiveUpAt = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media of message {MessageId} could not be copied (attempt {Attempt}, giving up: {GiveUp}): {Reason}.")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int attempt, bool giveUp, string reason);

    public static string KeyFor(Guid tenantId, Guid messageId) => $"messaging/{tenantId}/{messageId}";

    public static bool IsValidMetaMediaId(string? metaMediaId) =>
        !string.IsNullOrWhiteSpace(metaMediaId) && metaMediaId.Length <= MetaMediaIdMaxLength;

    public async Task CopyOneAsync(PendingMedia pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var now = clock.UtcNow;
        if (!IsValidMetaMediaId(pending.MetaMediaId))
        {
            await FailAsync(pending, "invalid_media_id", giveUp: true, cancellationToken);
            return;
        }

        if (now - pending.OccurredAt > MetaRetention)
        {
            await FailAsync(pending, "expired", giveUp: true, cancellationToken);
            return;
        }

        var sender = await connections.ResolveSenderAsync(pending.TenantId, pending.ConnectionId, cancellationToken);
        if (sender is null)
        {
            await FailAsync(pending, "connection_unavailable", giveUp: false, cancellationToken);
            return;
        }

        var info = await meta.GetMediaAsync(sender, pending.MetaMediaId, cancellationToken);
        if (!info.Succeeded || info.Value is null)
        {
            await FailAsync(pending, "media_info:" + (info.Failure?.Reason ?? "empty"), giveUp: false, cancellationToken);
            return;
        }

        var media = info.Value;
        if (media.FileSize > MaxBytes)
        {
            await FailAsync(pending, "too_large", giveUp: true, cancellationToken);
            return;
        }

        if (media.FileSize <= 0)
        {
            // Sin el largo no se puede subir a R2 (sin chunks firmados, ver IObjectStorage).
            await FailAsync(pending, "no_file_size", giveUp: false, cancellationToken);
            return;
        }

        var key = KeyFor(pending.TenantId, pending.MessageId);
        string hash;
        long length;
        try
        {
            await using var source = await meta.OpenMediaAsync(sender, media.Url, cancellationToken);
            await using var hashing = new Sha256PassThroughStream(source);
            await store.UploadAsync(key, hashing, media.FileSize, media.MimeType, cancellationToken);
            hash = hashing.FinishHex();
            length = hashing.BytesRead;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Sólo el tipo: el mensaje de una excepción de red puede traer la URL firmada.
            await FailAsync(pending, "copy:" + exception.GetType().Name, giveUp: false, cancellationToken);
            return;
        }

        if (length != media.FileSize
            || (media.Sha256 is { } expected && !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase)))
        {
            await store.DeleteAsync(key, cancellationToken);
            await FailAsync(pending, length != media.FileSize ? "size_mismatch" : "sha256_mismatch", giveUp: false, cancellationToken);
            return;
        }

        var mimeType = media.MimeType.Length <= 128 ? media.MimeType : media.MimeType[..128];
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.message_media
               SET stored_at = {now}, storage_key = {key}, size_bytes = {length}, sha256 = {hash}, mime_type = {mimeType}, last_error = NULL
             WHERE message_id = {pending.MessageId}
            """, cancellationToken);
    }

    private async Task FailAsync(PendingMedia pending, string reason, bool giveUp, CancellationToken cancellationToken)
    {
        LogFailed(logger, pending.MessageId, pending.Attempts, giveUp, reason);
        var lastError = reason.Length <= LastErrorMaxLength ? reason : reason[..LastErrorMaxLength];
        if (giveUp)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE messaging.message_media SET last_error = {lastError}, next_attempt_at = {GiveUpAt} WHERE message_id = {pending.MessageId}",
                cancellationToken);
            return;
        }

        // El reintento ya lo programó el reclamo (next_attempt_at = now + lease).
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE messaging.message_media SET last_error = {lastError} WHERE message_id = {pending.MessageId}",
            cancellationToken);
    }
}

using System.Net.Http.Headers;
using System.Security.Cryptography;
using BuildingBlocks.Application;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Media;

internal enum MediaTransferStatus
{
    /// <summary>Copiado y verificado contra el SHA-256 de Meta (o Meta no mandó ninguno).</summary>
    Stored,

    /// <summary>Copiado, pero la referencia de Meta no se pudo decodificar: se guarda el hash propio sin comparar.</summary>
    StoredUnverified,

    /// <summary>Falló este intento; el reclamo ya programó el siguiente.</summary>
    Retry,

    /// <summary>No tiene sentido volver a intentar.</summary>
    GiveUp,

    /// <summary>Se subió, pero el largo o el hash no coinciden: hay que borrar lo subido y reintentar.</summary>
    Mismatch,
}

internal sealed record MediaTransferResult(MediaTransferStatus Status, string? Reason, string Key, long Length, string? Sha256Hex, string MimeType)
{
    public static MediaTransferResult Failed(MediaTransferStatus status, string reason, string key) =>
        new(status, reason, key, 0, null, FallbackMimeType);

    public const string FallbackMimeType = "application/octet-stream";
}

/// <summary>
/// §8.6, «Copia», sin base de datos: token → GET /{id} → stream a R2 con SHA-256 al pasar. Separado de
/// <see cref="MediaCopyProcessor"/> para probar el tope de tiempo, el hash y los motivos sin Postgres.
/// Nunca devuelve ni registra el token ni la URL firmada: los motivos llevan, a lo sumo, el tipo de una excepción.
/// </summary>
internal sealed class MediaTransfer(
    IMessagingConnectionDirectory connections,
    IWhatsAppCloudClient meta,
    IInstagramClient instagram,
    IMessagingMediaStore store,
    IClock clock)
{
    /// <summary>Spec 2026-10-14 §3: el tope de un adjunto de Instagram. Se baja a memoria porque el CDN no siempre dice el largo, y R2
    /// lo exige antes de subir.</summary>
    public const long InstagramMaxBytes = 25L * 1024 * 1024;

    /// <summary>§8.6: el tope de WhatsApp para un documento. Más que eso no se intenta.</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>El ancho de <c>message_media.meta_media_id</c>.</summary>
    public const int MetaMediaIdMaxLength = 64;

    public const int MimeTypeMaxLength = 128;

    /// <summary>§8.6: Meta guarda el medio 7 días; pasado eso no hay qué bajar.</summary>
    public static readonly TimeSpan MetaRetention = TimeSpan.FromDays(7);

    /// <summary>Lo que dura una URL firmada de Meta y el tope de una copia: una copia trabada más que esto no
    /// congela la cola de la réplica; falla como <c>copy:timeout</c> y se reintenta. Todo lease de
    /// <see cref="MediaCopyWorker.Leases"/> es más largo, para que otra réplica no la retome a mitad de camino.</summary>
    public static readonly TimeSpan DefaultCopyTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Configurable sólo para las pruebas.</summary>
    public TimeSpan CopyTimeout { get; init; } = DefaultCopyTimeout;

    public static string KeyFor(Guid tenantId, Guid messageId) => MessagingMediaKeys.For(tenantId, messageId);

    public static bool IsValidMetaMediaId(string? metaMediaId) =>
        !string.IsNullOrWhiteSpace(metaMediaId) && metaMediaId.Length <= MetaMediaIdMaxLength;

    /// <summary>Un <c>mime_type</c> que no parsea (o no cabe en la columna) se guarda como octet-stream: al
    /// servirlo, un Content-Type inválido haría fallar la respuesta con 500.</summary>
    public static string NormalizeMimeType(string? mimeType) =>
        !string.IsNullOrWhiteSpace(mimeType)
        && mimeType.Length <= MimeTypeMaxLength
        && MediaTypeHeaderValue.TryParse(mimeType, out var parsed)
        && parsed.MediaType is { } mediaType
        && mediaType.Contains('/', StringComparison.Ordinal)
            ? mimeType.Trim()
            : MediaTransferResult.FallbackMimeType;

    /// <summary>
    /// Compara la referencia de Meta con el hash calculado. El <c>sha256</c> del webhook viene en base64 y el
    /// de <c>GET /{media-id}</c> no está documentado con certeza, así que se aceptan los dos: hex (64) y base64 (44).
    /// <c>null</c> = la referencia no se pudo decodificar.
    /// </summary>
    public static bool? Sha256Matches(string reference, byte[] computed)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var trimmed = reference.Trim();
        byte[]? expected = null;
        if (trimmed.Length == 64)
        {
            try
            {
                expected = Convert.FromHexString(trimmed);
            }
            catch (FormatException)
            {
                expected = null;
            }
        }

        if (expected is null && trimmed.Length == 44)
        {
            var buffer = new byte[33];
            expected = Convert.TryFromBase64String(trimmed, buffer, out var written) && written == 32 ? buffer[..32] : null;
        }

        return expected is null ? null : CryptographicOperations.FixedTimeEquals(expected, computed);
    }

    public async Task<MediaTransferResult> RunAsync(PendingMedia pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var key = KeyFor(pending.TenantId, pending.MessageId);
        if (pending.SourceUrl is { } sourceUrl)
        {
            return await RunInstagramAsync(pending, sourceUrl, key, cancellationToken);
        }

        if (!IsValidMetaMediaId(pending.MetaMediaId))
        {
            return MediaTransferResult.Failed(MediaTransferStatus.GiveUp, "invalid_media_id", key);
        }

        if (clock.UtcNow - pending.OccurredAt > MetaRetention)
        {
            return MediaTransferResult.Failed(MediaTransferStatus.GiveUp, "expired", key);
        }

        var sender = await connections.ResolveSenderAsync(pending.TenantId, pending.ConnectionId, cancellationToken);
        if (sender is null)
        {
            return MediaTransferResult.Failed(MediaTransferStatus.Retry, "connection_unavailable", key);
        }

        var info = await meta.GetMediaAsync(sender, pending.MetaMediaId, cancellationToken);
        if (!info.Succeeded || info.Value is null)
        {
            return MediaTransferResult.Failed(MediaTransferStatus.Retry, "media_info:" + (info.Failure?.Reason ?? "empty"), key);
        }

        var media = info.Value;
        if (media.FileSize > MaxBytes)
        {
            return MediaTransferResult.Failed(MediaTransferStatus.GiveUp, "too_large", key);
        }

        if (media.FileSize <= 0)
        {
            // Sin el largo no se puede subir a R2 (sin chunks firmados, ver IObjectStorage).
            return MediaTransferResult.Failed(MediaTransferStatus.Retry, "no_file_size", key);
        }

        var mimeType = NormalizeMimeType(media.MimeType);
        byte[] hash;
        long length;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(CopyTimeout);
            try
            {
                await using var source = await meta.OpenMediaAsync(sender, media.Url, timeout.Token);
                await using var hashing = new Sha256PassThroughStream(source);
                // Un stream de red que ignora el token se destraba cerrándolo.
                await using var unblock = timeout.Token.Register(static state => ((Stream)state!).Dispose(), hashing);
                await store.UploadAsync(key, hashing, media.FileSize, mimeType, timeout.Token);
                hash = hashing.FinishBytes();
                length = hashing.BytesRead;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Sólo el tipo: el mensaje de una excepción de red puede traer la URL firmada.
                var reason = timeout.IsCancellationRequested ? "copy:timeout" : "copy:" + exception.GetType().Name;
                return MediaTransferResult.Failed(MediaTransferStatus.Retry, reason, key);
            }
        }

        var hex = Convert.ToHexStringLower(hash);
        if (length != media.FileSize)
        {
            return new MediaTransferResult(MediaTransferStatus.Mismatch, "size_mismatch", key, length, hex, mimeType);
        }

        return Verified(media.Sha256, hash, key, length, hex, mimeType);
    }

    /// <summary>Spec 2026-10-14 §6.2: GET a la URL pública (sin token), a memoria con tope, y a R2 con el tipo que dijo el CDN. Sin
    /// hash de referencia: Meta no manda ninguno para Instagram, así que queda <c>Stored</c> con el hash propio.</summary>
    private async Task<MediaTransferResult> RunInstagramAsync(PendingMedia pending, string sourceUrl, string key, CancellationToken cancellationToken)
    {
        if (clock.UtcNow - pending.OccurredAt > MetaRetention)
        {
            return MediaTransferResult.Failed(MediaTransferStatus.GiveUp, "expired", key);
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return MediaTransferResult.Failed(MediaTransferStatus.GiveUp, "invalid_url", key);
        }

        byte[] content;
        string mimeType;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(CopyTimeout);
            try
            {
                var download = await instagram.OpenAsync(url, timeout.Token);
                if (download is null)
                {
                    return MediaTransferResult.Failed(MediaTransferStatus.Retry, "download:unavailable", key);
                }

                await using var source = download.Content;
                if (download.Length is { } declared && declared > InstagramMaxBytes)
                {
                    return MediaTransferResult.Failed(MediaTransferStatus.GiveUp, "too_large", key);
                }

                var read = await ReadCappedAsync(source, InstagramMaxBytes, timeout.Token);
                if (read is null)
                {
                    return MediaTransferResult.Failed(MediaTransferStatus.GiveUp, "too_large", key);
                }

                content = read;

                mimeType = NormalizeMimeType(download.ContentType);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var reason = timeout.IsCancellationRequested ? "copy:timeout" : "copy:" + exception.GetType().Name;
                return MediaTransferResult.Failed(MediaTransferStatus.Retry, reason, key);
            }
        }

        if (content.Length == 0)
        {
            return MediaTransferResult.Failed(MediaTransferStatus.Retry, "empty", key);
        }

        try
        {
            using var upload = new MemoryStream(content, writable: false);
            await store.UploadAsync(key, upload, content.Length, mimeType, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return MediaTransferResult.Failed(MediaTransferStatus.Retry, "upload:" + exception.GetType().Name, key);
        }

        return new MediaTransferResult(MediaTransferStatus.Stored, null, key, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), mimeType);
    }

    /// <summary><c>null</c> si el stream supera el tope.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static MediaTransferResult Verified(string? sha256, byte[] hash, string key, long length, string hex, string mimeType)
    {
        var matches = sha256 is { } reference ? Sha256Matches(reference, hash) : true;
        return matches switch
        {
            true => new MediaTransferResult(MediaTransferStatus.Stored, null, key, length, hex, mimeType),
            false => new MediaTransferResult(MediaTransferStatus.Mismatch, "sha256_mismatch", key, length, hex, mimeType),
            null => new MediaTransferResult(MediaTransferStatus.StoredUnverified, null, key, length, hex, mimeType),
        };
    }
}

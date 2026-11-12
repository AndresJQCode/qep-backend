using System.Security.Cryptography;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Lo que quedó de la preparación: el medio ya subido (o la falla de la subida) y los bytes finales si es una imagen.
/// <paramref name="Upload"/> es <c>null</c> en Instagram: Meta descarga el adjunto desde R2 (decisión 12), no hay subida previa.</summary>
internal sealed record PreparedOutboundMedia(
    string MimeType, string FileName, long SizeBytes, string? Sha256Hex, byte[]? Compressed, MessagingGraphResult<string>? Upload);

/// <summary>
/// La preparación de un adjunto saliente, sacada de <see cref="SendMediaMessageHandler"/> para que la comparta el envío
/// programado: leer el objeto una vez, comprobar el contenido, comprimir la imagen y subir a Meta.
/// </summary>
internal sealed class OutboundMediaPreparer(IWhatsAppCloudClient meta, IMessagingMediaStore store, IMessagingImageCompressor compressor)
{
    // Toda imagen sale como JPEG (R12); el multipart lleva un nombre genérico ASCII y el real viaja en document.filename (el de un
    // video o un audio no viaja: WhatsApp no lo muestra, queda en QEP).
    internal const string ImageMimeType = "image/jpeg";
    internal const string GenericImageName = "image.jpg";

    /// <summary>Un solo GET del origen: la cabecera decide el formato (R9) y el resto sigue por el mismo stream. La imagen se
    /// comprime y sube desde memoria (≤ 1600 px, unos cientos de KB); el documento va de R2 a Meta sin pasar por memoria, con el
    /// SHA-256 calculado al pasar (<c>null</c> si Meta cortó antes de leerlo entero). Con <paramref name="sender"/> en <c>null</c>
    /// (Instagram) no hay subida a Meta: el documento sólo se verifica y se mide. <paramref name="discardSource"/> borra el origen
    /// cuando el contenido no es válido.</summary>
    public async Task<PreparedOutboundMedia> PrepareAsync(
        MessagingSender? sender, string sourceKey, long sizeBytes, string fileName, string extension, Func<Task> discardSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discardSource);
        var opened = await store.OpenReadAsync(sourceKey, cancellationToken) ?? throw UploadNotFound();
        await using var source = opened.Content;
        var header = new byte[OutboundMediaSniffer.HeaderLength];
        var read = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (OutboundMediaSniffer.Detect(header.AsSpan(0, read), extension) is not { } format)
        {
            await discardSource();
            throw new MessagingDomainException(MessagingErrorCodes.MediaUnsupportedFormat, "The file content does not match its type.");
        }

        await using var whole = new PrefixedHashingStream(header.AsMemory(0, read), source);
        if (!OutboundMediaPolicy.IsImage(format))
        {
            var mimeType = OutboundMediaPolicy.MimeTypeOf(format);
            if (sender is null)
            {
                // Instagram: el documento se promueve en R2 tal cual; el hash no hace falta para enviar y no se lee entero acá.
                return new PreparedOutboundMedia(mimeType, fileName, sizeBytes, null, null, null);
            }

            var upload = await meta.UploadMediaAsync(sender, whole, sizeBytes, mimeType, GenericNameOf(OutboundMediaPolicy.KindOf(format)) + extension, cancellationToken);
            return new PreparedOutboundMedia(mimeType, fileName, sizeBytes, whole.BytesRead == sizeBytes ? whole.FinishHex() : null, null, upload);
        }

        var compressed = await compressor.CompressAsync(whole, cancellationToken);
        if (compressed is null)
        {
            await discardSource();
            throw new MessagingDomainException(MessagingErrorCodes.MediaInvalidImage, "The image could not be read.");
        }

        MessagingGraphResult<string>? uploaded = null;
        if (sender is not null)
        {
            using var content = new MemoryStream(compressed.Content, writable: false);
            uploaded = await meta.UploadMediaAsync(sender, content, compressed.Content.Length, ImageMimeType, GenericImageName, cancellationToken);
        }

        // Se guarda con el nombre base y .jpg: lo que viaja y lo que se guarda es la comprimida (R12).
        return new PreparedOutboundMedia(
            ImageMimeType, Path.GetFileNameWithoutExtension(fileName) + ".jpg", compressed.Content.Length,
            Convert.ToHexStringLower(SHA256.HashData(compressed.Content)), compressed.Content, uploaded);
    }

    /// <summary>Sube a Meta un objeto que ya se validó y se guardó (el ejecutor de un mensaje programado): sin sniff ni compresión,
    /// con el nombre genérico del multipart. Si el objeto ya no está, no hay qué enviar.</summary>
    public async Task<(MessagingGraphResult<string> Upload, string? Sha256Hex)> UploadStoredAsync(
        MessagingSender sender, string key, long sizeBytes, string mimeType, MessageKind kind, string extension, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var opened = await store.OpenReadAsync(key, cancellationToken)
            ?? throw new MessagingDomainException(MessagingErrorCodes.ScheduledMediaMissing, "The scheduled attachment no longer exists.");
        await using var source = opened.Content;
        await using var whole = new PrefixedHashingStream(ReadOnlyMemory<byte>.Empty, source);
        var fileName = kind == MessageKind.Image ? GenericImageName : GenericNameOf(kind) + extension;
        var upload = await meta.UploadMediaAsync(sender, whole, sizeBytes, mimeType, fileName, cancellationToken);
        return (upload, whole.BytesRead == sizeBytes ? whole.FinishHex() : null);
    }

    private static string GenericNameOf(MessageKind kind) => kind switch
    {
        MessageKind.Video => "video",
        MessageKind.Audio => "audio",
        _ => "document",
    };

    internal static MessagingDomainException UploadNotFound() =>
        new(MessagingErrorCodes.MediaUploadNotFound, "The upload does not exist, expired or was already used.");
}

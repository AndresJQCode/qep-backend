namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-09 §6.4 y §8.6: dónde vive la copia de un medio entrante. El adaptador está en
/// Bootstrapper, sobre el <c>IObjectStorage</c> de Storage (bucket privado): Messaging no referencia
/// Storage. Todo va por stream, porque un documento de WhatsApp llega a 100 MB.
/// </summary>
public interface IMessagingMediaStore
{
    /// <summary><paramref name="contentLength"/> es el <c>file_size</c> que reporta Meta: R2 necesita el largo antes de subir.</summary>
    Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken);

    /// <summary><c>null</c> si el objeto no está. El stream es de quien llama.</summary>
    Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-14 decisión 12: una URL firmada de lectura de vida corta, para que Meta descargue el adjunto de un
    /// saliente de Instagram (<c>attachment.payload.url</c>). No hay subida previa como en WhatsApp.</summary>
    Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-20 D10: el tamaño del objeto, para resolver un <c>Range</c>; <c>null</c> si no está. El adaptador lo mide
    /// con un stat; ésta abre el objeto y existe para que los dobles en memoria no tengan que implementarla.</summary>
    async Task<long?> SizeOfAsync(string key, CancellationToken cancellationToken)
    {
        if (await OpenReadAsync(key, cancellationToken) is not { } whole)
        {
            return null;
        }

        await whole.Content.DisposeAsync();
        return whole.Length;
    }

    /// <summary>D10: los bytes <c>[first, last]</c> (inclusive, ya resueltos con <see cref="MediaByteRange"/>); <c>null</c> si el objeto no
    /// está. El adaptador pide el rango a R2; ésta corta el objeto entero, como la de <see cref="SizeOfAsync"/>.</summary>
    async Task<MediaStreamDto?> OpenReadRangeAsync(string key, long first, long last, CancellationToken cancellationToken)
    {
        if (await OpenReadAsync(key, cancellationToken) is not { } whole)
        {
            return null;
        }

        var length = last - first + 1;
        return whole with { Content = await BuildingBlocks.Application.StreamSlice.CreateAsync(whole.Content, first, length, cancellationToken), Length = length };
    }
}

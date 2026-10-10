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
}

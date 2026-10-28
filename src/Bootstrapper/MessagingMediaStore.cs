using Modules.Messaging.Application;
using Modules.Storage.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-09 §6.4 y §6.6: el bucket privado de Storage por stream. Messaging no referencia
/// Storage. El tipo de contenido y el nombre que se sirven salen de la fila, no de acá.</summary>
internal sealed class MessagingMediaStore(IObjectStorage objectStorage) : IMessagingMediaStore
{
    public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
        objectStorage.UploadAsync(key, content, contentLength, contentType, cancellationToken);

    public async Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        await objectStorage.OpenReadAsync(key, cancellationToken) is { } opened
            ? new MediaStreamDto(opened.Content, opened.ContentType, opened.Length, null)
            : null;

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => objectStorage.DeleteAsync(key, cancellationToken);

    /// <summary>Spec 2026-10-14 decisión 12: sin nombre de archivo, la lee Meta y no una persona.</summary>
    public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        objectStorage.CreatePresignedDownloadUrlAsync(key, lifetime, null, cancellationToken);

    /// <summary>Spec 2026-10-20 D10: un stat, sin abrir el objeto.</summary>
    public async Task<long?> SizeOfAsync(string key, CancellationToken cancellationToken) =>
        await objectStorage.StatAsync(key, cancellationToken) is { } stat ? stat.SizeBytes : null;

    /// <summary>D10: el rango viaja en el GET a R2; no se lee el objeto entero por cada salto del reproductor.</summary>
    public async Task<MediaStreamDto?> OpenReadRangeAsync(string key, long first, long last, CancellationToken cancellationToken) =>
        await objectStorage.OpenReadRangeAsync(key, first, last, cancellationToken) is { } opened
            ? new MediaStreamDto(opened.Content, opened.ContentType, opened.Length, null)
            : null;
}

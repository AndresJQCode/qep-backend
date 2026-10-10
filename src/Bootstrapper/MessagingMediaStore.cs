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
}

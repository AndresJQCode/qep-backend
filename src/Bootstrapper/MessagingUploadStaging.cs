using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;

namespace Bootstrapper;

/// <summary>Messaging-media-reply R1 y R13: el staging del adjunto del asesor en el bucket privado de Storage. Messaging no
/// referencia Storage. La URL vive lo mismo que cualquier prefirmada del bucket (<c>Storage:PresignedUrlMinutes</c>) y el
/// <c>If-None-Match</c> lo firma <see cref="IObjectStorage.CreatePresignedUploadUrlAsync"/>.</summary>
internal sealed class MessagingUploadStaging(IObjectStorage objectStorage, IOptions<StorageOptions> options, IClock clock)
    : IMessagingUploadStaging
{
    public async Task<MessagingUploadUrl> CreateUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken)
    {
        // Se toma la hora antes de firmar: el aviso de expiración nunca promete más vida de la que tiene la URL.
        var expiresAt = clock.UtcNow.AddMinutes(options.Value.PresignedUrlMinutes);
        var url = await objectStorage.CreatePresignedUploadUrlAsync(key, contentType, cancellationToken);
        return new MessagingUploadUrl(url, expiresAt);
    }

    public async Task<StagedUpload?> StatAsync(string key, CancellationToken cancellationToken) =>
        await objectStorage.StatAsync(key, cancellationToken) is { } stored
            ? new StagedUpload(stored.SizeBytes, stored.Checksum)
            : null;

    public Task PromoteAsync(string stagingKey, string finalKey, string eTag, CancellationToken cancellationToken) =>
        objectStorage.PromoteAsync(stagingKey, finalKey, eTag, cancellationToken);
}

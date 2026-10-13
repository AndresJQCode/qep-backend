namespace Modules.Messaging.Application;

/// <summary>
/// Messaging-media-reply R1 y R13: el staging del adjunto del asesor en el bucket privado. El front sube directo con una URL
/// prefirmada y el servidor lo confirma después. El adaptador está en Bootstrapper, sobre el <c>IObjectStorage</c> de Storage:
/// Messaging no referencia Storage.
/// </summary>
public interface IMessagingUploadStaging
{
    /// <summary>PUT prefirmado que exige <paramref name="contentType"/> e <c>If-None-Match: *</c>: la clave no se pisa.</summary>
    Task<MessagingUploadUrl> CreateUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken);

    /// <summary><c>null</c> si el objeto no está (el PUT nunca ocurrió o el lifecycle ya lo borró).</summary>
    Task<StagedUpload?> StatAsync(string key, CancellationToken cancellationToken);

    /// <summary>Copia del lado del servidor, sin pasar los bytes por la API. Falla si el ETag del origen ya no es <paramref name="eTag"/>.</summary>
    Task PromoteAsync(string stagingKey, string finalKey, string eTag, CancellationToken cancellationToken);
}

public sealed record MessagingUploadUrl(Uri Url, DateTimeOffset ExpiresAt);

public sealed record StagedUpload(long SizeBytes, string ETag);

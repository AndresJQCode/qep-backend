using Modules.Messaging.Application;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Un solo diccionario para el staging y el R2 final: el documento se promueve copiando, la imagen se sube por stream. Lo comparten
/// el envío de adjuntos y el worker de programados (que guarda en <c>messaging/scheduled/</c> y copia a la clave final).</summary>
internal sealed class FakeMessagingBucket : IMessagingMediaStore, IMessagingUploadStaging
{
    public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Un tamaño distinto al real, para probar el tope sin escribir 100 MB.</summary>
    public Dictionary<string, long> SizeOverrides { get; } = new(StringComparer.Ordinal);

    public Task<MessagingUploadUrl> CreateUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(new MessagingUploadUrl(new Uri($"https://r2.test/{key}"), DateTimeOffset.UtcNow.AddMinutes(5)));

    public Task<StagedUpload?> StatAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<StagedUpload?>(Objects.TryGetValue(key, out var stored)
            ? new StagedUpload(SizeOverrides.TryGetValue(key, out var size) ? size : stored.Bytes.Length, "etag-" + stored.Bytes.Length)
            : null);

    public Task PromoteAsync(string stagingKey, string finalKey, string eTag, CancellationToken cancellationToken)
    {
        Objects[finalKey] = Objects[stagingKey];
        return Task.CompletedTask;
    }

    public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Objects[key] = (buffer.ToArray(), contentType);
    }

    public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(key, out var stored) ? new MediaStreamDto(new MemoryStream(stored.Bytes), stored.ContentType, stored.Bytes.Length, null) : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Objects.Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>Spec 2026-10-14 decisión 12: la URL que Meta bajaría; en pruebas apunta al handler falso de Graph.</summary>
    public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
}

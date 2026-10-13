using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>El staging del adjunto del asesor sobre el mismo bucket en memoria que el resto (<see cref="InMemoryMediaStore"/>): lo que
/// "subió el front" es un objeto en <c>Objects</c> bajo la clave de staging. La URL firmada es determinista (<see cref="UrlFor"/>) y la
/// expiración es la hora de la prueba más cinco minutos.</summary>
internal sealed class FakeUploadStaging(InMemoryMediaStore store) : IMessagingUploadStaging
{
    private static readonly DateTimeOffset Base = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);

    public const string ETag = "etag-1";

    public List<(string Key, string ContentType)> Signed { get; } = [];

    public List<string> Stats { get; } = [];

    public List<(string StagingKey, string FinalKey, string ETag)> Promoted { get; } = [];

    /// <summary>El PUT prefirmado no impone tope: lo que el front declaró y lo que subió de verdad pueden diferir. Si se fija, es lo
    /// que dice el stat sin importar los bytes.</summary>
    public long? StatSize { get; set; }

    public Exception? PromoteFailure { get; set; }

    public static Uri UrlFor(string key) => new($"https://r2.test/{key}");

    public Task<MessagingUploadUrl> CreateUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken)
    {
        Signed.Add((key, contentType));
        return Task.FromResult(new MessagingUploadUrl(UrlFor(key), Base.AddMinutes(5)));
    }

    public Task<StagedUpload?> StatAsync(string key, CancellationToken cancellationToken)
    {
        Stats.Add(key);
        return Task.FromResult(store.Objects.TryGetValue(key, out var stored) ? new StagedUpload(StatSize ?? stored.Bytes.Length, ETag) : null);
    }

    public Task PromoteAsync(string stagingKey, string finalKey, string eTag, CancellationToken cancellationToken)
    {
        if (PromoteFailure is { } failure)
        {
            throw failure;
        }

        Promoted.Add((stagingKey, finalKey, eTag));
        store.Objects[finalKey] = store.Objects[stagingKey];
        return Task.CompletedTask;
    }
}

/// <summary>El compresor sin ImageSharp: devuelve lo configurado y anota lo que recibió. <c>null</c> = no decodifica.</summary>
internal sealed class FakeImageCompressor : IMessagingImageCompressor
{
    public static readonly byte[] Output = [0xFF, 0xD8, 0xFF, 0xE1, 9, 9, 9, 9];

    public CompressedImage? Result { get; set; } = new(Output, 1600, 1200);

    public List<byte[]> Received { get; } = [];

    public async Task<CompressedImage?> CompressAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        Received.Add(buffer.ToArray());
        return Result;
    }
}

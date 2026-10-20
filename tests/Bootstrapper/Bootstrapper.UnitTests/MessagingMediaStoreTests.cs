using Modules.Storage.Application;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-20 D10: el adaptador del almacén de medios pasa el rango a Storage (un GET por rango en R2) y mide el
/// objeto con un stat, sin abrirlo.</summary>
public sealed class MessagingMediaStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RangeRecordingStorage _storage = new();

    [Fact]
    public async Task TheSizeComesFromTheStatAndAMissingObjectHasNone()
    {
        _storage.Stored["k"] = new StoredObject(1234, "etag");
        var store = new MessagingMediaStore(_storage);

        Assert.Equal(1234L, await store.SizeOfAsync("k", Ct));
        Assert.Null(await store.SizeOfAsync("nope", Ct));
        Assert.Empty(_storage.Ranges);
    }

    [Fact]
    public async Task ARangedReadAsksStorageForThatRange()
    {
        var store = new MessagingMediaStore(_storage);

        var opened = await store.OpenReadRangeAsync("k", 10, 19, Ct);

        Assert.Equal(("k", 10L, 19L), Assert.Single(_storage.Ranges));
        Assert.NotNull(opened);
        Assert.Equal((10L, "video/mp4"), (opened.Length, opened.ContentType));
    }

    private sealed class RangeRecordingStorage : IObjectStorage
    {
        public Dictionary<string, StoredObject> Stored { get; } = new(StringComparer.Ordinal);

        public List<(string Key, long From, long To)> Ranges { get; } = [];

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Stored.GetValueOrDefault(key));

        public Task<StoredObjectStream?> OpenReadRangeAsync(string key, long first, long last, CancellationToken cancellationToken)
        {
            Ranges.Add((key, first, last));
            return Task.FromResult<StoredObjectStream?>(new StoredObjectStream(new MemoryStream(new byte[last - first + 1]), "video/mp4", last - first + 1));
        }

        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // Abrir el objeto entero para medirlo o cortarlo es justo lo que el adaptador no debe hacer.
        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

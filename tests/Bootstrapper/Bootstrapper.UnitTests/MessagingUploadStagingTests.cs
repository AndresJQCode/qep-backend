using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure;

namespace Bootstrapper.UnitTests;

/// <summary>Messaging-media-reply R1 y R13: el staging del adjunto sobre el bucket privado de Storage. El adaptador
/// no decide nada: firma el PUT con el tipo exigido, mide el objeto y promueve con el ETag que vio.</summary>
public sealed class MessagingUploadStagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingObjectStorage _storage = new();

    private MessagingUploadStaging NewStaging(int presignedMinutes = 7) =>
        new(_storage, Options.Create(new StorageOptions { PresignedUrlMinutes = presignedMinutes }), new FixedClock(Now));

    [Fact]
    public async Task TheUploadUrlIsSignedWithTheContentTypeAndExpiresAfterThePresignedLifetime()
    {
        var url = await NewStaging().CreateUploadUrlAsync("messaging/staging/k.pdf", "application/pdf", TestContext.Current.CancellationToken);

        Assert.Equal("messaging/staging/k.pdf", _storage.PresignedKey);
        Assert.Equal("application/pdf", _storage.PresignedContentType);
        Assert.Equal(new Uri("https://r2.test/messaging/staging/k.pdf"), url.Url);
        Assert.Equal(Now.AddMinutes(7), url.ExpiresAt);
    }

    [Fact]
    public async Task StatReturnsNullWhenTheObjectIsMissing() =>
        Assert.Null(await NewStaging().StatAsync("nope", TestContext.Current.CancellationToken));

    [Fact]
    public async Task StatReturnsTheSizeAndTheETagOfTheStoredObject()
    {
        _storage.Stored["k"] = new StoredObject(1234, "abc");

        var staged = await NewStaging().StatAsync("k", TestContext.Current.CancellationToken);

        Assert.Equal(1234, staged!.SizeBytes);
        Assert.Equal("abc", staged.ETag);
    }

    [Fact]
    public async Task PromoteCopiesWithTheETagToMatch()
    {
        await NewStaging().PromoteAsync("from", "to", "etag-1", TestContext.Current.CancellationToken);

        Assert.Equal(("from", "to", "etag-1"), _storage.Promoted.Single());
    }

    private sealed class RecordingObjectStorage : IObjectStorage
    {
        public Dictionary<string, StoredObject> Stored { get; } = new(StringComparer.Ordinal);

        public List<(string Source, string Destination, string ETag)> Promoted { get; } = [];

        public string? PresignedKey { get; private set; }

        public string? PresignedContentType { get; private set; }

        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken)
        {
            PresignedKey = key;
            PresignedContentType = contentType;
            return Task.FromResult(new Uri($"https://r2.test/{key}"));
        }

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.GetValueOrDefault(key));

        public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken)
        {
            Promoted.Add((sourceKey, destinationKey, expectedChecksum));
            return Task.CompletedTask;
        }

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

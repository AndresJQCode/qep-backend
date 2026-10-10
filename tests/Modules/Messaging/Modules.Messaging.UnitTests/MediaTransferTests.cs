using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Media;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.6, «Copia», sin Postgres: el tope de tiempo por copia, el SHA-256 de Meta en
/// hex o en base64, los motivos para rendirse y que ningún motivo lleve el token ni la URL firmada.</summary>
public sealed class MediaTransferTests
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("imagen de prueba");
    private static readonly Uri SignedUrl = new("https://lookaside.test/m/1?sig=SIGNED-URL-SENTINEL");
    private const string Token = "meta-access-token-SENTINEL-unit";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MessagingTestBed _bed = new();
    private readonly RecordingMediaStore _store = new();

    private sealed class RecordingMediaStore : IMessagingMediaStore
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Objects[key] = (buffer.ToArray(), contentType);
        }

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Un cuerpo que nunca termina: cada lectura espera hasta que la cancelen.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private MediaTransfer Transfer(TimeSpan? copyTimeout = null)
    {
        _bed.Connections.Sender = new MessagingSender("111", Token);
        return new MediaTransfer(_bed.Connections, _bed.Meta, _store, new FakeClock(_bed.Now))
        {
            CopyTimeout = copyTimeout ?? MediaTransfer.DefaultCopyTimeout,
        };
    }

    private PendingMedia Pending(string metaMediaId = "media-1", DateTimeOffset? occurredAt = null) =>
        new(Guid.CreateVersion7(), _bed.TenantId, _bed.ConnectionId, metaMediaId, occurredAt ?? _bed.Now.AddMinutes(-1), 1);

    private void MetaAnswers(string? sha256, long? fileSize = null, string mimeType = "image/jpeg")
    {
        _bed.Meta.MediaInfo = new MessagingGraphResult<MediaInfo>(new MediaInfo(SignedUrl, mimeType, sha256, fileSize ?? Bytes.Length), null);
        _bed.Meta.OpenMedia = (_, _) => new MemoryStream(Bytes);
    }

    [Fact]
    public async Task AStalledDownloadFailsAsCopyTimeoutWithinTheBound()
    {
        MetaAnswers(null);
        _bed.Meta.OpenMedia = (_, _) => new StalledStream();
        var stopwatch = Stopwatch.StartNew();

        var result = await Transfer(TimeSpan.FromMilliseconds(200)).RunAsync(Pending(), Ct);

        Assert.Equal((MediaTransferStatus.Retry, "copy:timeout"), (result.Status, result.Reason));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
        Assert.Empty(_store.Objects);
    }

    [Theory]
    [InlineData("hex")]
    [InlineData("HEX")]
    [InlineData("base64")]
    public async Task AHexOrBase64ReferenceIsVerified(string format)
    {
        var hash = SHA256.HashData(Bytes);
        MetaAnswers(format switch
        {
            "hex" => Convert.ToHexStringLower(hash),
            "HEX" => Convert.ToHexString(hash),
            _ => Convert.ToBase64String(hash),
        });

        var result = await Transfer().RunAsync(Pending(), Ct);

        Assert.Equal(MediaTransferStatus.Stored, result.Status);
        Assert.Equal(Convert.ToHexStringLower(hash), result.Sha256Hex);
        Assert.Equal(Bytes, Assert.Single(_store.Objects.Values).Bytes);
    }

    [Theory]
    [InlineData("hex")]
    [InlineData("base64")]
    public async Task AWrongHexOrBase64ReferenceIsAMismatch(string format)
    {
        var wrong = SHA256.HashData(Encoding.UTF8.GetBytes("otra cosa"));
        MetaAnswers(format == "hex" ? Convert.ToHexStringLower(wrong) : Convert.ToBase64String(wrong));

        var result = await Transfer().RunAsync(Pending(), Ct);

        Assert.Equal((MediaTransferStatus.Mismatch, "sha256_mismatch"), (result.Status, result.Reason));
    }

    [Theory]
    [InlineData("deadbeef")]
    [InlineData("c2hhMjU2")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task AnUndecodableReferenceStoresTheLocalHashUnverified(string reference)
    {
        MetaAnswers(reference);

        var result = await Transfer().RunAsync(Pending(), Ct);

        Assert.Equal(MediaTransferStatus.StoredUnverified, result.Status);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes)), result.Sha256Hex);
    }

    [Fact]
    public async Task MoreThan100MbGivesUpWithoutDownloading()
    {
        MetaAnswers(null, MediaTransfer.MaxBytes + 1);
        var opened = false;
        _bed.Meta.OpenMedia = (_, _) =>
        {
            opened = true;
            return new MemoryStream(Bytes);
        };

        var result = await Transfer().RunAsync(Pending(), Ct);

        Assert.Equal((MediaTransferStatus.GiveUp, "too_large"), (result.Status, result.Reason));
        Assert.False(opened);
    }

    [Fact]
    public async Task OlderThanSevenDaysGivesUpWithoutAskingMeta()
    {
        MetaAnswers(null);

        var result = await Transfer().RunAsync(Pending(occurredAt: _bed.Now - MediaTransfer.MetaRetention - TimeSpan.FromMinutes(1)), Ct);

        Assert.Equal((MediaTransferStatus.GiveUp, "expired"), (result.Status, result.Reason));
        Assert.Empty(_bed.Meta.MediaRequests);
    }

    [Fact]
    public async Task ADownloadErrorRecordsOnlyItsTypeNeverTheTokenOrTheUrl()
    {
        MetaAnswers(null);
        _bed.Meta.OpenMedia = (url, _) => throw new HttpRequestException($"GET {url} with Bearer {Token} failed");

        var result = await Transfer().RunAsync(Pending(), Ct);

        Assert.Equal((MediaTransferStatus.Retry, "copy:HttpRequestException"), (result.Status, result.Reason));
        Assert.DoesNotContain(Token, result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("lookaside", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("application/pdf; charset=binary", "application/pdf; charset=binary")]
    [InlineData("not a mime", "application/octet-stream")]
    [InlineData("text/html\r\nX-Injected: 1", "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    public async Task AnUnparseableMimeTypeIsStoredAsOctetStream(string fromMeta, string stored)
    {
        MetaAnswers(null, mimeType: fromMeta);

        var result = await Transfer().RunAsync(Pending(), Ct);

        Assert.Equal(MediaTransferStatus.Stored, result.Status);
        Assert.Equal(stored, result.MimeType);
        Assert.Equal(stored, Assert.Single(_store.Objects.Values).ContentType);
    }
}

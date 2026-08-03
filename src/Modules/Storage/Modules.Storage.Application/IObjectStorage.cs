namespace Modules.Storage.Application;

// Outbound port to Cloudflare R2 through its S3-compatible API (ADR 0020).
// Presigned URLs are short-lived and issued only after authorization is (re-)evaluated.
public interface IObjectStorage
{
    Task<Uri> CreatePresignedUploadUrlAsync(
        string key,
        string contentType,
        CancellationToken cancellationToken);

    Task<Uri> CreatePresignedDownloadUrlAsync(
        string key,
        CancellationToken cancellationToken);

    // Metadata of the stored object, or null if the object is absent (upload never happened).
    Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    Task PromoteAsync(
        string sourceKey,
        string destinationKey,
        string expectedChecksum,
        CancellationToken cancellationToken);

    // Server-side read/write, for backend processes that need the bytes directly (e.g. a
    // module parsing an uploaded file import, or writing a generated report) rather than
    // handing a presigned URL to a browser. Same bucket/credentials as the presigned-URL
    // path; just a different access pattern for a non-browser caller.
    Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken);

    Task UploadAsync(
        string key, byte[] content, string contentType, CancellationToken cancellationToken);
}

public sealed record StoredObject(long SizeBytes, string Checksum);

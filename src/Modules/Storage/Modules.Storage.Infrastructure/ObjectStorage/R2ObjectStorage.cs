using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.ObjectStorage;

// Adaptador de Cloudflare R2 sobre el cliente AWSSDK.S3 compatible con S3 (ADR 0020). Las
// URLs prefirmadas las emite R2 directamente; el cliente nunca ve credenciales.
internal sealed class R2ObjectStorage(IAmazonS3 client, IOptions<StorageOptions> options)
    : IObjectStorage
{
    private string Bucket => options.Value.R2.Bucket;

    private TimeSpan Expiry => TimeSpan.FromMinutes(options.Value.PresignedUrlMinutes);

    public async Task<Uri> CreatePresignedUploadUrlAsync(
        string key, string contentType, CancellationToken cancellationToken)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(Expiry),
        };
        request.Headers["If-None-Match"] = "*";
        var url = await client.GetPreSignedURLAsync(request);
        return new Uri(url);
    }

    public async Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, CancellationToken cancellationToken)
    {
        var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(Expiry),
        });
        return new Uri(url);
    }

    public async Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await client.GetObjectMetadataAsync(Bucket, key, cancellationToken);
            var checksum = (metadata.ETag ?? string.Empty).Trim('"');
            return new StoredObject(metadata.ContentLength, checksum);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        client.DeleteObjectAsync(Bucket, key, cancellationToken);

    public async Task PromoteAsync(
        string sourceKey,
        string destinationKey,
        string expectedChecksum,
        CancellationToken cancellationToken)
    {
        await client.CopyObjectAsync(
            new CopyObjectRequest
            {
                SourceBucket = Bucket,
                SourceKey = sourceKey,
                DestinationBucket = Bucket,
                DestinationKey = destinationKey,
                ETagToMatch = expectedChecksum,
            },
            cancellationToken);
    }

    public async Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(Bucket, key, cancellationToken);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    public async Task UploadAsync(
        string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content);
        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = key,
                InputStream = stream,
                ContentType = contentType,
                // R2 no implementa el cuerpo firmado en chunks: responde 500
                // "STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented". El SDK lo usa por
                // defecto cuando la carga va por InputStream, así que hay que apagarlo a mano.
                // Con la firma de payload desactivada el cuerpo viaja como UNSIGNED-PAYLOAD,
                // que es lo que R2 espera; la integridad la sigue dando TLS, y el llamador
                // verifica el ETag contra el checksum que ya conoce.
                UseChunkEncoding = false,
                DisablePayloadSigning = true,
            },
            cancellationToken);
    }
}

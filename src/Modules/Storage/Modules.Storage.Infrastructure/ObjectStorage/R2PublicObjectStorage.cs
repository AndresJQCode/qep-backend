using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.ObjectStorage;

internal sealed class R2PublicObjectStorage(IAmazonS3 client, IOptions<StorageOptions> options)
    : IPublicObjectStorage
{
    private R2Options Settings => options.Value.R2;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Settings.PublicBucket) &&
        !string.IsNullOrWhiteSpace(Settings.PublicBaseUrl);

    public Task CopyFromPrivateAsync(
        string privateKey, string publicKey, CancellationToken cancellationToken) =>
        client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Settings.Bucket,
            SourceKey = privateKey,
            DestinationBucket = Settings.PublicBucket,
            DestinationKey = publicKey,
        }, cancellationToken);

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
        IsConfigured
            ? client.DeleteObjectAsync(Settings.PublicBucket, publicKey, cancellationToken)
            : Task.CompletedTask;

    public async Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken)
    {
        // Sin bucket público no pudo haberse hecho ninguna copia.
        if (!IsConfigured)
        {
            return false;
        }

        // HEAD, igual que StatAsync en R2ObjectStorage: sólo el 404 es «no existe»; cualquier otro error
        // se propaga y el llamador reintenta.
        try
        {
            await client.GetObjectMetadataAsync(Settings.PublicBucket, publicKey, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public string GetUrl(string publicKey)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Public image storage is not configured.");
        }

        return $"{Settings.PublicBaseUrl.TrimEnd('/')}/{publicKey}";
    }

    public async Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken)
    {
        // Sin prefijo se recorrería el bucket entero, con las imágenes de producto y los PDF de
        // cotización incluidos (spec 2026-09-16, D12).
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var response = await client.ListObjectsV2Async(
            new ListObjectsV2Request
            {
                BucketName = Settings.PublicBucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
            },
            cancellationToken);

        // AWSSDK.S3 v4 deja las colecciones en null cuando la respuesta no trae elementos, y
        // LastModified es opcional. Sin fecha no se puede probar que un objeto sea viejo, y la
        // reconciliación sólo borra lo viejo: se deja afuera en vez de inventarle una.
        var objects = (response.S3Objects ?? [])
            .Where(entry => entry.LastModified is not null)
            .Select(entry => new PublicStoredObject(entry.Key, ToUtc(entry.LastModified!.Value)))
            .ToArray();

        return new PublicObjectPage(
            objects,
            response.IsTruncated == true ? response.NextContinuationToken : null);
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());
}

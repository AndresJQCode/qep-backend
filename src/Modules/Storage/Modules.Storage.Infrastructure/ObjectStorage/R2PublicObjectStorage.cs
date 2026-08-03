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

    public string GetUrl(string publicKey)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Public image storage is not configured.");
        }

        return $"{Settings.PublicBaseUrl.TrimEnd('/')}/{publicKey}";
    }
}

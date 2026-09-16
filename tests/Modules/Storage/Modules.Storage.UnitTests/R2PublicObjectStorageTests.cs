using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Modules.Storage.Infrastructure;
using Modules.Storage.Infrastructure.ObjectStorage;

namespace Modules.Storage.UnitTests;

/// <summary>
/// El listado del bucket público por prefijo (spec 2026-09-16, D12) contra el cliente S3, sin red:
/// qué bucket y prefijo pide, cómo sigue la paginación y cómo trata las dos rarezas de AWSSDK.S3 v4
/// (colecciones en null y fechas opcionales).
/// </summary>
public sealed class R2PublicObjectStorageTests
{
    [Fact]
    public async Task ListingMapsAPageOfTheRequestedPrefix()
    {
        var modified = new DateTime(2026, 9, 14, 10, 30, 0, DateTimeKind.Utc);
        using var client = new ListingS3Client
        {
            Response = new ListObjectsV2Response
            {
                S3Objects =
                [
                    new S3Object { Key = "payment-proofs/a.pdf", LastModified = modified },
                    new S3Object { Key = "payment-proofs/sin-fecha.pdf" },
                ],
                IsTruncated = true,
                NextContinuationToken = "next-page",
            },
        };

        var page = await StorageWith(client).ListAsync(
            "payment-proofs/", "this-page", TestContext.Current.CancellationToken);

        Assert.NotNull(client.Captured);
        Assert.Equal("qep-public", client.Captured.BucketName);
        Assert.Equal("payment-proofs/", client.Captured.Prefix);
        Assert.Equal("this-page", client.Captured.ContinuationToken);
        // Sin fecha no se puede probar que sea viejo, y la reconciliación sólo borra lo viejo.
        var stored = Assert.Single(page.Objects);
        Assert.Equal("payment-proofs/a.pdf", stored.Key);
        Assert.Equal(new DateTimeOffset(modified), stored.LastModified);
        Assert.Equal("next-page", page.ContinuationToken);
    }

    // AWSSDK.S3 v4 deja S3Objects en null cuando la página no trae objetos.
    [Fact]
    public async Task AnEmptyLastPageHasNoObjectsAndNoToken()
    {
        using var client = new ListingS3Client { Response = new ListObjectsV2Response() };

        var page = await StorageWith(client).ListAsync(
            "payment-proofs/", continuationToken: null, TestContext.Current.CancellationToken);

        Assert.Empty(page.Objects);
        Assert.Null(page.ContinuationToken);
        Assert.NotNull(client.Captured);
        Assert.Null(client.Captured.ContinuationToken);
    }

    // Sin prefijo se recorrería el bucket entero, con las imágenes de producto y los PDF de
    // cotización incluidos (D12).
    [Fact]
    public async Task ABlankPrefixIsRejectedWithoutCallingR2()
    {
        using var client = new ListingS3Client();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            StorageWith(client).ListAsync(" ", continuationToken: null, TestContext.Current.CancellationToken));

        Assert.Null(client.Captured);
    }

    private static R2PublicObjectStorage StorageWith(IAmazonS3 client) =>
        new(client, Options.Create(new StorageOptions
        {
            R2 = new R2Options
            {
                Bucket = "qep-private",
                PublicBucket = "qep-public",
                PublicBaseUrl = "https://assets-qep.example.co",
            },
        }));

    // Doble a mano, como R2ObjectStorageTests: hereda del cliente real y se queda con el request.
    private sealed class ListingS3Client : AmazonS3Client
    {
        public ListingS3Client()
            : base(
                new BasicAWSCredentials("key", "secret"),
                new AmazonS3Config
                {
                    ServiceURL = "https://example.invalid",
                    ForcePathStyle = true,
                    AuthenticationRegion = "auto",
                })
        {
        }

        public ListObjectsV2Response Response { get; init; } = new();

        public ListObjectsV2Request? Captured { get; private set; }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(
            ListObjectsV2Request request,
            CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult(Response);
        }
    }
}

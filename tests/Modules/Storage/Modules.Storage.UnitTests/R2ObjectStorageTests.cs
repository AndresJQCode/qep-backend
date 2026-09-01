using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Storage.Infrastructure;
using Modules.Storage.Infrastructure.ObjectStorage;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Cloudflare R2 es compatible con S3 **casi**, y las dos diferencias que importan están acá.
/// `AWSSDK.S3` v4 asume comportamiento de S3 que R2 no implementa, y el resultado es un 500 que
/// el cliente ve como `server.unexpected`:
///
/// <list type="bullet">
/// <item>el checksum en trailer HTTP → `STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER not implemented`;</item>
/// <item>el cuerpo firmado en chunks → `STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented`.</item>
/// </list>
///
/// **Ninguna prueba anterior podía verlos**, porque las pruebas sustituyen `IObjectStorage` por un
/// doble en memoria y la subida del navegador va por URL prefirmada, sin tocar el SDK. Sólo se
/// manifiestan contra R2 real, en `complete`, que es la primera escritura que el backend hace con
/// el cliente S3. Estas dos pruebas existen para que apagar cualquiera de los cuatro
/// interruptores vuelva a ponerse rojo acá y no en producción.
/// </summary>
public sealed class R2ObjectStorageTests
{
    [Fact]
    public async Task UploadDisablesChunkEncodingAndPayloadSigning()
    {
        using var client = new CapturingS3Client();
        var storage = new R2ObjectStorage(client, Options.Create(new StorageOptions
        {
            R2 = new R2Options { Bucket = "qep-private" },
        }));

        await storage.UploadAsync(
            "files/tenants/x/variants/thumbnail.webp",
            [1, 2, 3],
            "image/webp",
            TestContext.Current.CancellationToken);

        var request = Assert.IsType<PutObjectRequest>(client.Captured);
        Assert.False(request.UseChunkEncoding);
        Assert.True(request.DisablePayloadSigning);
    }

    [Fact]
    public async Task DownloadUrlHonoursTheRequestedExpiryAndFileName()
    {
        using var client = new CapturingS3Client();
        var storage = new R2ObjectStorage(client, Options.Create(new StorageOptions
        {
            R2 = new R2Options { Bucket = "qep-private" },
        }));

        var url = await storage.CreatePresignedDownloadUrlAsync(
            "exports/tenants/x/2026/08/report.xlsx",
            TimeSpan.FromHours(24),
            "clientes-20260831-101500.xlsx",
            TestContext.Current.CancellationToken);

        var query = Uri.UnescapeDataString(url.Query);
        Assert.Contains("X-Amz-Expires=86400", query, StringComparison.Ordinal);
        Assert.Contains("attachment", query, StringComparison.Ordinal);
        Assert.Contains("clientes-20260831-101500.xlsx", query, StringComparison.Ordinal);
    }

    // Sin nombre de descarga no se manda el override: la cabecera vacía haría que el navegador
    // muestre el objeto en vez de bajarlo, que es peor que no decir nada.
    [Fact]
    public async Task DownloadUrlOmitsContentDispositionWhenNoFileNameIsGiven()
    {
        using var client = new CapturingS3Client();
        var storage = new R2ObjectStorage(client, Options.Create(new StorageOptions
        {
            R2 = new R2Options { Bucket = "qep-private" },
        }));

        var url = await storage.CreatePresignedDownloadUrlAsync(
            "exports/tenants/x/2026/08/report.xlsx",
            TimeSpan.FromHours(1),
            downloadFileName: null,
            TestContext.Current.CancellationToken);

        var query = Uri.UnescapeDataString(url.Query);
        Assert.Contains("X-Amz-Expires=3600", query, StringComparison.Ordinal);
        Assert.DoesNotContain("response-content-disposition", query, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientIsBuiltWithoutChecksumTrailers()
    {
        var services = new ServiceCollection();
        services.AddStorageInfrastructure(Configuration());
        using var provider = services.BuildServiceProvider();

        var config = Assert.IsType<AmazonS3Client>(provider.GetRequiredService<IAmazonS3>()).Config;

        Assert.Equal(
            RequestChecksumCalculation.WHEN_REQUIRED,
            Assert.IsType<AmazonS3Config>(config).RequestChecksumCalculation);
        Assert.Equal(
            ResponseChecksumValidation.WHEN_REQUIRED,
            Assert.IsType<AmazonS3Config>(config).ResponseChecksumValidation);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] =
                    "Host=localhost;Port=5432;Database=unit_tests;Username=x;Password=x",
                ["Storage:R2:AccountId"] = "account",
                ["Storage:R2:AccessKeyId"] = "key",
                ["Storage:R2:SecretAccessKey"] = "secret",
                ["Storage:R2:Bucket"] = "qep-private",
            })
            .Build();

    // Doble a mano, como el resto del repositorio: no hay librería de mocking. Hereda del cliente
    // real porque `IAmazonS3` tiene demasiada superficie para implementarla entera, y lo único
    // que hace falta es quedarse con el request sin que salga a la red.
    private sealed class CapturingS3Client : AmazonS3Client
    {
        public CapturingS3Client()
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

        public PutObjectRequest? Captured { get; private set; }

        public override Task<PutObjectResponse> PutObjectAsync(
            PutObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult(new PutObjectResponse());
        }
    }
}

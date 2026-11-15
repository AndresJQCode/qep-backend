using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Storage.Application;
using Npgsql;
using static Modules.Catalog.IntegrationTests.CatalogTestDatabase;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// La sonda <c>catalog</c> responde si alguna galería, de cualquier producto, todavía tiene el
/// archivo. Storage la consulta antes de purgar una foto liberada.
/// </summary>
public sealed class ProductGalleryFileReferenceProbeTests(SharedCatalogHost sharedHost) : IClassFixture<SharedCatalogHost>
{
    private string TenantId { get; } = Guid.CreateVersion7().ToString();
    private const string SubjectId = "01900000-0000-7000-8000-000000000012";
    private const string PublicBaseUrl = "https://cdn.qep.test";

    private static readonly string[] All =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        StoragePermissions.FileRead,
        StoragePermissions.FileUpload
    ];

    [Fact]
    public async Task TrueWhileAGalleryHasTheFile()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        var database = sharedHost.Database;
        using var client = CreateClient(factory);
        var file = await UploadImageAsync(client, database, "una.png");
        await CreateWithGalleryAsync(client, "Vela", "VS-001", file);

        Assert.True(await HasReferencesAsync(factory, file));
    }

    [Fact]
    public async Task FalseForAFileInNoGallery()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        var database = sharedHost.Database;
        using var client = CreateClient(factory);
        var file = await UploadImageAsync(client, database, "suelta.png");

        Assert.False(await HasReferencesAsync(factory, file));
    }

    [Fact]
    public async Task TrueWhileAnotherGalleryStillHasTheFile()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        var database = sharedHost.Database;
        using var client = CreateClient(factory);
        var file = await UploadImageAsync(client, database, "compartida.png");
        var first = await CreateWithGalleryAsync(client, "Vela", "VS-001", file);
        await CreateWithGalleryAsync(client, "Jabón", "JB-001", file);

        await PutWithoutGalleryAsync(client, first);

        Assert.True(await HasReferencesAsync(factory, file));
    }

    [Fact]
    public async Task TheGalleryFileIndexExists()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        var database = sharedHost.Database;
        using var client = CreateClient(factory);
        // El cliente fuerza el arranque del host, que aplica las migraciones.
        await UploadImageAsync(client, database, "una.png");

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM pg_indexes WHERE schemaname = 'catalog' " +
            "AND indexname = 'IX_product_gallery_images_file'",
            connection);

        Assert.NotNull(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<bool> HasReferencesAsync(QepApiFactory factory, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var probe = Assert.Single(
            scope.ServiceProvider.GetServices<IFileReferenceProbe>(),
            candidate => candidate.Source == "catalog");
        return await probe.HasReferencesAsync(fileId, TestContext.Current.CancellationToken);
    }

    private async Task<ProductResponse> CreateWithGalleryAsync(
        HttpClient client,
        string name,
        string sku,
        params Guid[] gallery)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name,
                gallery,
                variants = new[] { ProductBodies.Variant(null, sku, ProductBodies.UsdPricing()) }
            },
            TestContext.Current.CancellationToken);
        return await ReadProductAsync(response);
    }

    private async Task PutWithoutGalleryAsync(HttpClient client, ProductResponse current)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{current.Id}",
            new
            {
                expectedVersion = current.Version,
                name = current.Name,
                gallery = Array.Empty<Guid>(),
                variants = new[]
                {
                    ProductBodies.Variant(current.Only().Id, "VS-001", ProductBodies.UsdPricing())
                }
            },
            TestContext.Current.CancellationToken);
        await ReadProductAsync(response);
    }

    private static async Task<ProductResponse> ReadProductAsync(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    // El estado Available se fuerza por SQL: completar la subida exige R2 y el escáner, que no
    // existen en una prueba (ver ProductImageApiTests.UploadFileAsync).
    private async Task<Guid> UploadImageAsync(
        HttpClient client,
        TestDatabase database,
        string name)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/files",
            new { ownerId = Guid.CreateVersion7(), ownerType = "Product", name, mimeType = "image/png", sizeBytes = 2048 },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var fileId = (await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).GetProperty("fileResourceId").GetGuid();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE storage.file_resources SET status = 'Available' WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        return fileId;
    }

    private HttpClient CreateClient(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', All));
        return client;
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Storage:R2:PublicBucket", "test-public-bucket");
            builder.UseSetting("Storage:R2:PublicBaseUrl", PublicBaseUrl);
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}

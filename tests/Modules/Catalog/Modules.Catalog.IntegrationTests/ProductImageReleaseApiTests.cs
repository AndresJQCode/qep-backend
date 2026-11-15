using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Modules.Storage.Application;
using Npgsql;
using static Modules.Catalog.IntegrationTests.CatalogTestDatabase;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Quitar fotos de la galería con el PUT libera sus archivos: el evento
/// <c>catalog.product.images-released.v1</c> se escribe en el outbox, en la misma transacción que
/// el producto, y lo consume Storage para purgarlos. Reordenar o dejar la galería igual no libera
/// nada.
/// </summary>
public sealed class ProductImageReleaseApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000011";
    private const string SubjectId = "01900000-0000-7000-8000-000000000012";
    private const string PublicBaseUrl = "https://cdn.qep.test";
    private const string EventName = "catalog.product.images-released.v1";

    private static readonly string[] All =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        StoragePermissions.FileRead,
        StoragePermissions.FileUpload
    ];

    [Fact]
    public async Task RemovingAPhotoWritesTheReleaseEventWithThatFileId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (a, b) = await TwoImagesAsync(client, database);
        var product = await CreateWithGalleryAsync(client, a, b);

        await PutAsync(client, product, "Vela", a);

        var payloads = await ReadEventPayloadsAsync(database);
        var payload = JsonDocument.Parse(Assert.Single(payloads)).RootElement;
        Assert.Equal(Guid.Parse(TenantId), payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(
            [b],
            payload.GetProperty("fileIds").EnumerateArray().Select(id => id.GetGuid()).ToArray());
    }

    [Fact]
    public async Task ReorderingTheGalleryWritesNoReleaseEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (a, b) = await TwoImagesAsync(client, database);
        var product = await CreateWithGalleryAsync(client, a, b);

        await PutAsync(client, product, "Vela", b, a);

        Assert.Empty(await ReadEventPayloadsAsync(database));
    }

    [Fact]
    public async Task AnUnchangedGalleryWritesNoReleaseEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (a, b) = await TwoImagesAsync(client, database);
        var product = await CreateWithGalleryAsync(client, a, b);

        await PutAsync(client, product, "Vela renombrada", a, b);

        Assert.Empty(await ReadEventPayloadsAsync(database));
    }

    private static async Task<(Guid A, Guid B)> TwoImagesAsync(
        HttpClient client,
        TestDatabase database) =>
        (await UploadImageAsync(client, database, "una.png"),
            await UploadImageAsync(client, database, "otra.png"));

    private static async Task<ProductResponse> CreateWithGalleryAsync(HttpClient client, params Guid[] gallery)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name = "Vela",
                gallery,
                variants = new[] { ProductBodies.Variant(null, "VS-001", ProductBodies.UsdPricing()) }
            },
            TestContext.Current.CancellationToken);
        return await ReadProductAsync(response);
    }

    private static async Task PutAsync(
        HttpClient client,
        ProductResponse current,
        string name,
        params Guid[] gallery)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{current.Id}",
            new
            {
                expectedVersion = current.Version,
                name,
                gallery,
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

    private static async Task<IReadOnlyList<string>> ReadEventPayloadsAsync(TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name",
            connection);
        command.Parameters.AddWithValue("name", EventName);

        var payloads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    // El estado Available se fuerza por SQL: completar la subida exige R2 y el escáner, que no
    // existen en una prueba (ver ProductImageApiTests.UploadFileAsync).
    private static async Task<Guid> UploadImageAsync(
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

    private static HttpClient CreateClient(QepApiFactory factory)
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

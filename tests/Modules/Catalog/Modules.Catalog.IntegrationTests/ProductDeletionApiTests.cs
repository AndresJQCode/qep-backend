using System.Net;
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
/// <c>DELETE /products/{productId}</c> (spec 2026-11-08): borra el producto para siempre, con todo lo que cuelga de
/// él, en una sola transacción con su auditoría y el aviso de las fotos que quedan sin dueño. Lo que cruza a
/// cotizaciones, pedidos y la caja se prueba en Quotations y Pos.
/// </summary>
public sealed class ProductDeletionApiTests(SharedCatalogHost sharedHost) : IClassFixture<SharedCatalogHost>
{
    private string TenantId { get; } = Guid.CreateVersion7().ToString();
    private const string SubjectId = "01900000-0000-7000-8000-000000000012";
    private string OtherTenantId { get; } = Guid.CreateVersion7().ToString();
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000ed";
    private const string PublicBaseUrl = "https://cdn.qep.test";
    private const string ImagesReleased = "catalog.product.images-released.v1";

    private static readonly string[] All =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        StoragePermissions.FileRead,
        StoragePermissions.FileUpload
    ];

    private static readonly string[] Sizes = ["S", "M"];

    // Las tablas que cuelgan del producto, cada una con la columna que la ata a él (directa o por la variante).
    private static readonly (string Table, string Filter)[] ProductRows =
    [
        ("products", "id = @id"),
        ("product_variants", "product_id = @id"),
        ("product_prices", "variant_id = ANY(@variants)"),
        ("product_price_scales", "variant_id = ANY(@variants)"),
        ("product_options", "product_id = @id"),
        ("product_variant_option_values", "variant_id = ANY(@variants)"),
        ("product_gallery_images", "product_id = @id"),
        ("product_price_changes", "product_id = @id")
    ];

    [Fact]
    public async Task DeleteWithoutIfMatchIs428()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var product = await CreatePlainAsync(client, "VS-001");

        var response = await DeleteAsync(client, product.Id, ifMatch: null);

        await AssertProblemAsync(response, HttpStatusCode.PreconditionRequired, "precondition.if_match_required");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, product.Id)).StatusCode);
    }

    [Fact]
    public async Task DeleteWithAStaleVersionIs412()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var product = await CreatePlainAsync(client, "VS-001");

        var response = await DeleteAsync(client, product.Id, $"\"{product.Version + 1}\"");

        await AssertProblemAsync(response, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, product.Id)).StatusCode);
    }

    // El de otro tenant responde lo mismo que uno que no existe: distinguirlos confirmaría que el id existe en otro lado.
    [Fact]
    public async Task DeleteOfAnUnknownProductOrOneOfAnotherTenantIs404()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, All);
        var foreign = await CreatePlainAsync(other, "AJ-001", OtherTenantId);

        var unknown = await DeleteAsync(client, Guid.CreateVersion7(), "\"1\"");
        var ofAnotherTenant = await DeleteAsync(client, foreign.Id, "\"1\"");

        await AssertProblemAsync(unknown, HttpStatusCode.NotFound, "catalog.product.not_found");
        await AssertProblemAsync(ofAnotherTenant, HttpStatusCode.NotFound, "catalog.product.not_found");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(other, foreign.Id, OtherTenantId)).StatusCode);
    }

    [Fact]
    public async Task DeleteUnderAnotherTenantRouteIs403()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, All);
        var foreign = await CreatePlainAsync(other, "AJ-001", OtherTenantId);

        var response = await DeleteAsync(client, foreign.Id, "\"1\"", OtherTenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(other, foreign.Id, OtherTenantId)).StatusCode);
    }

    [Fact]
    public async Task DeleteWithoutManagePermissionIs403()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = CreateClient(factory, SubjectId, TenantId, All);
        using var reader = CreateClient(factory, SubjectId, TenantId, CatalogPermissions.ProductRead);
        var product = await CreatePlainAsync(manager, "VS-001");

        var response = await DeleteAsync(reader, product.Id, $"\"{product.Version}\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(manager, product.Id)).StatusCode);
    }

    [Fact]
    public async Task DeleteRemovesTheProductAndEverythingHangingFromIt()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        var database = sharedHost.Database;
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var (product, _) = await CreateFullAsync(client, database);
        var variants = product.Variants.Select(variant => variant.Id).ToArray();
        // La prueba no pasa por tablas vacías: antes del DELETE cada una tiene filas del producto.
        foreach (var (table, filter) in ProductRows)
        {
            Assert.True(await CountAsync(database, table, filter, product.Id, variants) > 0, $"catalog.{table} sin filas antes del DELETE.");
        }

        var response = await DeleteAsync(client, product.Id, $"\"{product.Version}\"");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        foreach (var (table, filter) in ProductRows)
        {
            Assert.Equal(0L, await CountAsync(database, table, filter, product.Id, variants));
        }

        var fetched = await GetAsync(client, product.Id);
        await AssertProblemAsync(fetched, HttpStatusCode.NotFound, "catalog.product.not_found");
    }

    [Fact]
    public async Task DeleteAcceptsAWeakEtag()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var product = await CreatePlainAsync(client, "VS-001");

        var response = await DeleteAsync(client, product.Id, $"W/\"{product.Version}\"");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteWritesTheImagesReleasedEventWithTheGalleryFileIds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var (product, photos) = await CreateFullAsync(client, database);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, product.Id, $"\"{product.Version}\"")).StatusCode);

        var payload = JsonDocument.Parse(Assert.Single(await ReadEventPayloadsAsync(database, ImagesReleased))).RootElement;
        Assert.Equal(Guid.Parse(TenantId), payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(
            photos.Order().ToArray(),
            payload.GetProperty("fileIds").EnumerateArray().Select(id => id.GetGuid()).Order().ToArray());
    }

    // Review Focus 1: el publicador no escribe nada con una lista vacía, y un producto sin fotos no tiene qué liberar.
    [Fact]
    public async Task DeletingAnUnusedProductWithoutImagesWritesNoImageEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var product = await CreatePlainAsync(client, "VS-001");

        var response = await DeleteAsync(client, product.Id, $"\"{product.Version}\"");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ReadEventPayloadsAsync(database, ImagesReleased));
    }

    [Fact]
    public async Task DeleteIsAudited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var product = await CreatePlainAsync(client, "VS-001");

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, product.Id, $"\"{product.Version}\"")).StatusCode);

        var audits = (await ReadEventPayloadsAsync(database, "platform.audit.recorded.v1"))
            .Select(json => JsonDocument.Parse(json).RootElement)
            .Where(payload => payload.GetProperty("action").GetString() == "catalog.product.deleted")
            .ToArray();
        var audit = Assert.Single(audits);
        Assert.Equal(product.Id.ToString(), audit.GetProperty("resourceId").GetString());
        Assert.Equal("success", audit.GetProperty("outcome").GetString());
        Assert.Equal(Guid.Parse(SubjectId), audit.GetProperty("actorId").GetGuid());
    }

    // ---- Helpers -------------------------------------------------------------------------

    private string ProductUrl(Guid productId, string? tenantId = null) =>
        $"/api/v1/tenants/{tenantId ?? TenantId}/catalog/products/{productId}";

    private Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, Guid productId, string? ifMatch, string? tenantId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, ProductUrl(productId, tenantId ?? TenantId));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> GetAsync(HttpClient client, Guid productId, string? tenantId = null) =>
        client.GetAsync(ProductUrl(productId, tenantId ?? TenantId), TestContext.Current.CancellationToken);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(status == response.StatusCode, $"Se esperaba {(int)status} y llegó {(int)response.StatusCode}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<ProductResponse> CreatePlainAsync(HttpClient client, string code, string? tenantId = null) =>
        await ReadProductAsync(await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId ?? TenantId}/catalog/products",
            ProductBodies.Create("Vela de soja", code),
            TestContext.Current.CancellationToken));

    /// <summary>
    /// Camiseta con Talla S/M: dos variantes con precio y escalas, galería de dos fotos (M elige la segunda) y un cambio
    /// de precio por <c>PUT</c>, para que cada tabla que cuelga del producto tenga filas. Devuelve el producto leído
    /// después del <c>PUT</c> y las dos fotos.
    /// </summary>
    private async Task<(ProductResponse Product, Guid[] Photos)> CreateFullAsync(
        HttpClient client, TestDatabase database)
    {
        var option = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/options",
            new { name = "Talla", values = Sizes },
            TestContext.Current.CancellationToken);
        Assert.True(option.IsSuccessStatusCode, await option.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var talla = await option.Content.ReadFromJsonAsync<OptionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(talla);
        var a = await UploadImageAsync(client, database, "una.png");
        var b = await UploadImageAsync(client, database, "otra.png");

        object Pricing(decimal usd) => new
        {
            prices = new Dictionary<string, decimal> { ["USD"] = usd },
            scales = new[] { new { fromUnit = 10, toUnit = 49, discount = 5m, restriction = "multiple", multiple = 1 } }
        };

        var created = await ReadProductAsync(await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name = "Camiseta",
                optionIds = new[] { talla.Id },
                gallery = new[] { a, b },
                variants = new object[]
                {
                    new { code = "CAM-S", optionValueIds = new[] { talla.Values[0].Id }, pricing = Pricing(10m) },
                    new { code = "CAM-M", optionValueIds = new[] { talla.Values[1].Id }, pricing = Pricing(12m), galleryFileId = b }
                }
            },
            TestContext.Current.CancellationToken));

        // Sube el precio de S: deja una fila en product_price_changes.
        var updated = await ReadProductAsync(await client.PutAsJsonAsync(
            ProductUrl(created.Id),
            new
            {
                expectedVersion = created.Version,
                name = "Camiseta",
                optionIds = new[] { talla.Id },
                gallery = new[] { a, b },
                variants = new object[]
                {
                    new { id = created.Variants[0].Id, code = "CAM-S", pricing = Pricing(11m) },
                    new { id = created.Variants[1].Id, code = "CAM-M", galleryFileId = b }
                }
            },
            TestContext.Current.CancellationToken));
        return (updated, [a, b]);
    }

    private static async Task<long> CountAsync(
        TestDatabase database, string table, string filter, Guid productId, Guid[] variantIds)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM catalog.{table} WHERE {filter}", connection);
        command.Parameters.AddWithValue("id", productId);
        command.Parameters.AddWithValue("variants", variantIds);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<IReadOnlyList<string>> ReadEventPayloadsAsync(TestDatabase database, string eventName)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name ORDER BY occurred_at",
            connection);
        command.Parameters.AddWithValue("name", eventName);

        var payloads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    private static async Task<ProductResponse> ReadProductAsync(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    // El estado Available se fuerza por SQL: completar la subida exige R2 y el escáner, que no existen en una prueba
    // (ver ProductImageApiTests.UploadFileAsync).
    private async Task<Guid> UploadImageAsync(HttpClient client, TestDatabase database, string name)
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

    private static HttpClient CreateClient(
        QepApiFactory factory, string subjectId, string tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
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

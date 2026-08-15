using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// CAT-05a — la imagen de producto, verificada de punta a punta contra PostgreSQL real.
///
/// Las reglas ya tienen prueba unitaria en `ProductImageResolverTests`. Lo que se verifica acá es
/// otra cosa: **que el cableado exista**. Un resolver perfecto que nadie llama pasa todas las
/// unitarias.
/// </summary>
public sealed class ProductImageApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000011";
    private const string SubjectId = "01900000-0000-7000-8000-000000000012";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ee";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000ed";

    // Sin bucket público configurado, IsConfigured es false y no habría URL que verificar:
    // R2PublicObjectStorage la arma desde estos dos valores. El validador de opciones exige que
    // vayan de a dos, así que se fijan los dos o ninguno.
    private const string PublicBaseUrl = "https://cdn.qep.test";

    private static readonly string[] All =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        StoragePermissions.FileRead,
        StoragePermissions.FileUpload
    ];

    /// <summary>
    /// CA-CAT-05-01 — la fuga entre tenants, y la razón de existir del slice.
    ///
    /// El archivo existe **de verdad**, en el otro tenant. No hay foreign key que lo impida:
    /// `image_file_id` es referencia blanda a `Storage` y no puede tener constraint sin cruzar la
    /// frontera de módulos. Sin la comprobación del handler, esto responde un `201` normal.
    /// </summary>
    [Fact]
    public async Task AnImageFromAnotherTenantIsRejectedAndNothingIsPersisted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, All);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, All);

        var foreignImage = await UploadImageAsync(other, OtherTenantId, database, "ajena.png");

        var response = await CreateProductAsync(owner, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            imageFileId = foreignImage
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "catalog.product.image_not_found",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        Assert.Empty(await ListAsync(owner, TenantId));
    }

    // CA-CAT-05-02: mismo código que el de otro tenant, a propósito. Distinguirlos confirmaría
    // que el id existe en otro lado.
    [Fact]
    public async Task AnUnknownImageIsRejectedWithTheSameCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            imageFileId = Guid.CreateVersion7()
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "catalog.product.image_not_found",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // CA-CAT-05-03: recién abrir la sesión de carga deja el archivo en PendingUpload. Una portada
    // que todavía no se subió mostraría un hueco en la ficha del producto.
    [Fact]
    public async Task AnImageThatHasNotFinishedUploadingIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var pending = await CreateUploadSessionAsync(client, TenantId, "pendiente.png", "image/png");

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            imageFileId = pending
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "catalog.product.image_not_available",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // CA-CAT-05-04: la regla es de catalog. FileUploadPolicy acepta PDF porque es la lista blanca
    // de storage, que también guarda documentos.
    [Fact]
    public async Task AFileThatIsNotAnImageIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var pdf = await UploadFileAsync(
            client, TenantId, database, "ficha.pdf", "application/pdf");

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            imageFileId = pdf
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "catalog.product.image_not_an_image",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // CA-CAT-05-05: el camino feliz. Sin esta prueba, un resolver que rechace todo pasaría las
    // otras cuatro.
    [Fact]
    public async Task AnAvailableImageFromTheSameTenantIsAcceptedAndPersisted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var image = await UploadImageAsync(client, TenantId, database, "portada.png");

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            imageFileId = image
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.Equal(image, created.ImageFileId);

        var fetched = await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            TestContext.Current.CancellationToken));
        Assert.Equal(image, fetched.ImageFileId);
    }

    // CA-CAT-05-06: sigue siendo opcional, y el PUT la puede limpiar. La regla nueva no debe
    // convertir un campo opcional en obligatorio.
    [Fact]
    public async Task ANullImageIsStillAcceptedAndThePutCanClearIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var image = await UploadImageAsync(client, TenantId, database, "portada.png");

        var withoutImage = await ReadProductAsync(await CreateProductAsync(client, TenantId, new
        {
            name = "Sin portada",
            code = "SP-001"
        }));
        Assert.Null(withoutImage.ImageFileId);

        var assigned = await ReadProductAsync(await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{withoutImage.Id}",
            new { name = "Sin portada", code = "SP-001", imageFileId = image },
            TestContext.Current.CancellationToken));
        Assert.Equal(image, assigned.ImageFileId);

        var cleared = await ReadProductAsync(await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{withoutImage.Id}",
            new { name = "Sin portada", code = "SP-001" },
            TestContext.Current.CancellationToken));
        Assert.Null(cleared.ImageFileId);
    }

    // CA-CAT-05-07: hoy este archivo quedaba como User, porque el endpoint caía en silencio a ese
    // valor. La aserción es sobre lo que quedó guardado, no sobre el status.
    [Fact]
    public async Task AFileCanDeclareThatItBelongsToAProduct()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/files",
            new
            {
                ownerId = Guid.CreateVersion7(),
                ownerType = "Product",
                name = "portada.png",
                mimeType = "image/png",
                sizeBytes = 2048
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var fileId = (await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).GetProperty("fileResourceId").GetGuid();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT owner_type FROM storage.file_resources WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);

        // La columna guarda el NOMBRE, no el número: StorageDbContext mapea el enum con
        // HasConversion<string>() sobre character varying(20).
        var ownerType = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.Equal(nameof(FileOwnerType.Product), Assert.IsType<string>(ownerType));
    }

    // CA-CAT-05-08: el fallback silencioso se termina. Antes esto devolvía 201 con owner_type=1.
    [Theory]
    [InlineData("Producto")]
    [InlineData("")]
    [InlineData("4")]
    public async Task AnUnknownOwnerTypeIsRejectedInsteadOfFallingBackToUser(string ownerType)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/files",
            new
            {
                ownerId = Guid.CreateVersion7(),
                ownerType,
                name = "portada.png",
                mimeType = "image/png",
                sizeBytes = 2048
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "storage.file.owner_type_invalid",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // CA-CAT-05-09 y CA-CAT-05-10: la URL depende de que la imagen esté publicada, y el
    // `imageFileId` viaja siempre — el cliente lo necesita para el PUT.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheProductExposesTheImageUrlOnlyWhenTheImageIsPublished(bool published)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var image = await UploadImageAsync(client, TenantId, database, "portada.png");
        if (published)
        {
            await PublishAsync(database, image);
        }

        var created = await ReadProductAsync(await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            imageFileId = image
        }));

        var fetched = await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            TestContext.Current.CancellationToken));

        Assert.Equal(image, fetched.ImageFileId);
        if (published)
        {
            Assert.Equal($"{PublicBaseUrl}/public/{image}.png", fetched.ImageUrl);
        }
        else
        {
            Assert.Null(fetched.ImageUrl);
        }
    }

    /// <summary>
    /// CA-CAT-05-11 — la razón de ser de `CAT-05b`.
    ///
    /// Sin esto, pintar una grilla de productos obliga al cliente a pedir una URL de descarga por
    /// cada uno. El listado los trae resueltos, y el producto sin portada viene con los dos campos
    /// en `null` sin costar una consulta.
    /// </summary>
    [Fact]
    public async Task TheListResolvesTheImageUrlOfEveryProduct()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var first = await UploadImageAsync(client, TenantId, database, "una.png");
        var second = await UploadImageAsync(client, TenantId, database, "otra.png");
        await PublishAsync(database, first);
        await PublishAsync(database, second);

        await CreateProductAsync(client, TenantId, new
        {
            name = "Con portada",
            code = "CP-001",
            imageFileId = first
        });
        await CreateProductAsync(client, TenantId, new
        {
            name = "Con otra portada",
            code = "CP-002",
            imageFileId = second
        });
        await CreateProductAsync(client, TenantId, new { name = "Sin portada", code = "SP-001" });

        var products = await ListAsync(client, TenantId);

        Assert.Equal(3, products.Count);
        Assert.Equal(
            $"{PublicBaseUrl}/public/{first}.png",
            products.Single(product => product.Code == "CP-001").ImageUrl);
        Assert.Equal(
            $"{PublicBaseUrl}/public/{second}.png",
            products.Single(product => product.Code == "CP-002").ImageUrl);

        var withoutImage = products.Single(product => product.Code == "SP-001");
        Assert.Null(withoutImage.ImageFileId);
        Assert.Null(withoutImage.ImageUrl);
    }

    /// <summary>
    /// Publica por SQL, por la misma razón que `UploadFileAsync` fuerza el estado: el endpoint de
    /// publicación copia el objeto en R2, que en una prueba no existe. Lo que hace falta acá es la
    /// **consecuencia** de publicar —que haya `public_storage_key`—, no el viaje a R2.
    /// </summary>
    private static async Task PublishAsync(PostgreSqlContainer database, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE storage.file_resources
            SET public_storage_key = @key, published_at = now()
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", fileId);
        command.Parameters.AddWithValue("key", $"public/{fileId}.png");
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Abre la sesión de carga y devuelve el id. El archivo queda en `PendingUpload`: subir el
    /// binario va contra R2, que en pruebas no existe.
    /// </summary>
    private static async Task<Guid> CreateUploadSessionAsync(
        HttpClient client,
        string tenantId,
        string name,
        string mimeType)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.CreateVersion7(),
                ownerType = "Product",
                name,
                mimeType,
                sizeBytes = 2048
            },
            TestContext.Current.CancellationToken);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return (await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).GetProperty("fileResourceId").GetGuid();
    }

    /// <summary>
    /// Deja un archivo en `Available`, que es el estado que las reglas exigen.
    ///
    /// **El estado se fuerza por SQL, y es deliberado.** Completar la subida de verdad exige
    /// escribir el binario en R2 y que el escáner lo apruebe; ninguna de las dos cosas existe en
    /// una prueba. Lo que este archivo verifica es la regla de `catalog`, no el ciclo de subida de
    /// `Storage`, que tiene sus propias pruebas.
    /// </summary>
    private static async Task<Guid> UploadFileAsync(
        HttpClient client,
        string tenantId,
        PostgreSqlContainer database,
        string name,
        string mimeType)
    {
        var fileId = await CreateUploadSessionAsync(client, tenantId, name, mimeType);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE storage.file_resources SET status = 'Available' WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        return fileId;
    }

    private static Task<Guid> UploadImageAsync(
        HttpClient client,
        string tenantId,
        PostgreSqlContainer database,
        string name) =>
        UploadFileAsync(client, tenantId, database, name, "image/png");

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client,
        string tenantId,
        object payload) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            payload,
            TestContext.Current.CancellationToken);

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

    private static async Task<IReadOnlyCollection<ProductResponse>> ListAsync(
        HttpClient client,
        string tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ProductsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Items;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

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
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}

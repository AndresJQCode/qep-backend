using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Modules.Storage.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Variantes, Tarea 5: crear, ver, listar y editar un producto con opciones, variantes y galería,
/// de punta a punta contra PostgreSQL. Las reglas del agregado y de <c>ProductVariantChanges</c>
/// tienen sus unitarias; acá se verifica el cableado: la biblioteca del tenant, Storage, la
/// versión y la forma BFF de la respuesta.
/// </summary>
public sealed class ProductVariantsApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000051";
    private const string SubjectId = "01900000-0000-7000-8000-000000000052";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000dd";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000dc";
    private const string PublicBaseUrl = "https://cdn.qep.test";

    private static readonly string[] All =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        StoragePermissions.FileRead,
        StoragePermissions.FileUpload
    ];

    // Un producto sin opciones se crea con una sola variante y se lee «como hoy»: su variante lleva
    // el id del producto (igual que uno migrado), el nombre visible es el del producto y no hay
    // opciones ni galería.
    [Fact]
    public async Task CreateWithoutOptionsReturnsSingleVariantLikeToday()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var created = await ReadProductAsync(await PostProductAsync(
            client, TenantId, ProductBodies.Create("Vela de soja", "VS-001", ProductBodies.UsdPricing(12m))));

        var variant = created.Only();
        Assert.Equal(created.Id, variant.Id);
        Assert.Equal("Vela de soja", variant.Name);
        Assert.Equal("VS-001", variant.Code);
        Assert.Null(variant.Barcode);
        Assert.Empty(variant.OptionValueIds);
        Assert.Equal(12m, variant.Prices["USD"]);
        Assert.True(variant.IsActive);
        // Tarea 6: recién creada, ninguna línea puede venderla.
        Assert.True(variant.CanDelete);
        Assert.Empty(created.Options);
        Assert.Empty(created.Gallery);
        Assert.Equal(1, created.Version);
    }

    // Tres opciones de la biblioteca, en el orden del producto: el nombre visible de cada variante
    // ya viene armado (spec §3.4, guion largo y barras) y las opciones traen sólo los valores que
    // usa alguna variante, en el orden de la biblioteca.
    [Fact]
    public async Task CreateThreeOptionsReturnsVisibleNamesInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M", "L");
        var color = await CreateOptionAsync(client, TenantId, "Color", "Azul", "Rojo");
        var manga = await CreateOptionAsync(client, TenantId, "Manga", "Corta", "Larga");

        var response = await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta básica",
            optionIds = new[] { talla.Id, color.Id, manga.Id },
            variants = new[]
            {
                VariantBody("CAM-M-AZ-C", ValueOf(talla, "M"), ValueOf(color, "Azul"), ValueOf(manga, "Corta")),
                VariantBody("CAM-S-RO-L", ValueOf(talla, "S"), ValueOf(color, "Rojo"), ValueOf(manga, "Larga")),
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.Equal(1, created.Version);
        Assert.Equal(["Talla", "Color", "Manga"], created.Options.Select(option => option.Name));
        Assert.Equal(["Camiseta básica – M / Azul / Corta", "Camiseta básica – S / Rojo / Larga"],
            created.Variants.Select(variant => variant.Name));
        // Primero la variante que toma el id del producto, como la por defecto de uno sin opciones.
        Assert.Equal(created.Id, created.Variants[0].Id);
        // Sólo los valores usados, en el orden de la biblioteca (S antes que M aunque M vino primero).
        Assert.Equal(["S", "M"], created.Options[0].Values.Select(value => value.Name));

        var fetched = await GetProductAsync(client, TenantId, created.Id);
        Assert.Equal(created.Variants.Select(variant => variant.Name), fetched.Variants.Select(variant => variant.Name));
    }

    // Enfoque de revisión 1. «Camiseta» con historial de precio (una variante por defecto con el id
    // del producto, como la deja la migración AddProductVariants) a la que se le agrega Talla: la
    // variante por defecto se convierte en «S» y conserva id, código, precio y su fila de historial;
    // M y L nacen con el precio copiado de ella y sin historial propio.
    [Fact]
    public async Task AddingOptionsToAMigratedProductKeepsItsVariantPricesAndHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var created = await ReadProductAsync(await PostProductAsync(
            client, TenantId, ProductBodies.Create("Camiseta", "CAM-001", ProductBodies.UsdPricing(20m))));
        var priced = await ReadProductAsync(await PutProductAsync(
            client, TenantId, created.Id, ProductBodies.Update(created, "Camiseta", "CAM-001", ProductBodies.UsdPricing(25m))));
        Assert.Equal(1, await CountHistoryAsync(database, created.Id));

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M", "L");
        var response = await PutProductAsync(client, TenantId, created.Id, new
        {
            expectedVersion = priced.Version,
            name = "Camiseta",
            optionIds = new[] { talla.Id },
            variants = new object[]
            {
                // Sin valores ni precio: toma el primer valor de Talla y conserva el suyo.
                new { id = created.Id, code = "CAM-001" },
                new { code = "CAM-M", optionValueIds = new[] { ValueOf(talla, "M") } },
                new { code = "CAM-L", optionValueIds = new[] { ValueOf(talla, "L") } },
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadProductAsync(response);
        Assert.Equal(3, updated.Variants.Count);

        var converted = updated.Variants[0];
        Assert.Equal(created.Id, converted.Id);
        Assert.Equal("CAM-001", converted.Code);
        Assert.Equal(25m, converted.Prices["USD"]);
        Assert.Equal([ValueOf(talla, "S")], converted.OptionValueIds);
        Assert.Equal("Camiseta – S", converted.Name);
        Assert.All(updated.Variants.Skip(1), added => Assert.Equal(25m, added.Prices["USD"]));

        // La fila de historial sigue en su variante, y el PUT no agregó ninguna: nada cambió de precio.
        Assert.Equal(1, await CountHistoryAsync(database, created.Id));
        Assert.Equal(1, await CountHistoryAsync(database, created.Id, variantId: created.Id));
    }

    // Enfoque de revisión 2. Dos personas abren el mismo producto (versión 1). La primera guarda
    // variantes y galería; la segunda guarda después con la versión vieja y recibe 412
    // concurrency.conflict —el código que el repositorio usa para todo PUT con versión—, y el
    // producto queda entero como lo dejó la primera, no a medias.
    [Fact]
    public async Task PutStaleVersionReturns412Concurrency()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var created = await ReadProductAsync(await PostProductAsync(
            client, TenantId, ProductBodies.Create("Vela de soja", "VS-001")));
        var image = await UploadImageAsync(client, TenantId, database, "portada.png");

        var first = await PutProductAsync(
            client, TenantId, created.Id, ProductBodies.Update(created, "Vela de soja", "VS-001", imageFileId: image));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PutProductAsync(
            client, TenantId, created.Id, ProductBodies.Update(created, "Vela pisada", "VS-999"));

        Assert.Equal(HttpStatusCode.PreconditionFailed, second.StatusCode);
        Assert.Equal("concurrency.conflict", await ReadCodeAsync(second));

        var current = await GetProductAsync(client, TenantId, created.Id);
        Assert.Equal("Vela de soja", current.Name);
        Assert.Equal("VS-001", current.Only().Code);
        Assert.Equal(image, Assert.Single(current.Gallery).FileId);
        Assert.Equal(2, current.Version);
    }

    // CAT-05 con galería: cada foto es referencia blanda a Storage. Sin la comprobación del handler,
    // una foto de otro tenant entra con 200. Mismo código que la portada de antes.
    [Fact]
    public async Task PutGalleryFileOfOtherTenantRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, All);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, All);

        var created = await ReadProductAsync(await PostProductAsync(
            owner, TenantId, ProductBodies.Create("Vela de soja", "VS-001")));
        var own = await UploadImageAsync(owner, TenantId, database, "propia.png");
        var foreign = await UploadImageAsync(other, OtherTenantId, database, "ajena.png");

        var response = await PutProductAsync(owner, TenantId, created.Id, new
        {
            expectedVersion = created.Version,
            name = "Vela de soja",
            gallery = new[] { own, foreign },
            variants = new[] { ProductBodies.Variant(created.Id, "VS-001", ProductBodies.UsdPricing()) }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("catalog.product.image_not_found", await ReadCodeAsync(response));
        Assert.Empty((await GetProductAsync(owner, TenantId, created.Id)).Gallery);
    }

    // El detalle trae la galería en orden con sus URLs, y cada variante su foto: la suya si eligió
    // una, la principal del producto si no (spec §3.3), ya resuelta para que la pantalla no repita
    // la regla.
    [Fact]
    public async Task GetReturnsGalleryAndVariantImage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var main = await UploadImageAsync(client, TenantId, database, "principal.png");
        var blue = await UploadImageAsync(client, TenantId, database, "azul.png");
        await PublishAsync(database, main);
        await PublishAsync(database, blue);
        var color = await CreateOptionAsync(client, TenantId, "Color", "Azul", "Rojo");

        var created = await ReadProductAsync(await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { color.Id },
            gallery = new[] { main, blue },
            variants = new object[]
            {
                new
                {
                    code = "CAM-AZ",
                    optionValueIds = new[] { ValueOf(color, "Azul") },
                    pricing = ProductBodies.UsdPricing(),
                    galleryFileId = blue
                },
                VariantBody("CAM-RO", ValueOf(color, "Rojo")),
            }
        }));

        var fetched = await GetProductAsync(client, TenantId, created.Id);

        Assert.Equal([main, blue], fetched.Gallery.Select(image => image.FileId));
        Assert.Equal(
            [$"{PublicBaseUrl}/public/{main}.png", $"{PublicBaseUrl}/public/{blue}.png"],
            fetched.Gallery.Select(image => image.Url));
        Assert.Equal(main, fetched.ImageFileId);
        Assert.Equal($"{PublicBaseUrl}/public/{main}.png", fetched.ImageUrl);

        var withPhoto = fetched.Variants[0];
        Assert.Equal(blue, withPhoto.GalleryFileId);
        Assert.Equal(fetched.Gallery[1].Id, withPhoto.GalleryImageId);
        Assert.Equal($"{PublicBaseUrl}/public/{blue}.png", withPhoto.ImageUrl);

        var withoutPhoto = fetched.Variants[1];
        Assert.Null(withoutPhoto.GalleryFileId);
        Assert.Null(withoutPhoto.GalleryImageId);
        Assert.Equal($"{PublicBaseUrl}/public/{main}.png", withoutPhoto.ImageUrl);
    }

    // Pendiente de la Tarea 2: el dominio no ve la biblioteca. Una opción de otro tenant no llega a
    // la FK (sería 500): sale 422 con su código y el campo marcado en el mapa `errors`.
    [Fact]
    public async Task AnOptionOfAnotherTenantIsRejectedWithTheFieldMarked()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, All);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, All);

        var foreign = await CreateOptionAsync(other, OtherTenantId, "Talla", "S");

        var response = await PostProductAsync(owner, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { foreign.Id },
            variants = new[] { VariantBody("CAM-S", ValueOf(foreign, "S")) }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("catalog.option.not_found", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("OptionIds[0]", out _));
        Assert.Empty((await ListAsync(owner, TenantId)).Items);
    }

    // La FK sólo dice que el valor existe; que sea de la opción de su posición lo revisa el handler.
    [Fact]
    public async Task AValueOfAnotherOptionIsRejectedOnItsPosition()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S");
        var color = await CreateOptionAsync(client, TenantId, "Color", "Azul");

        var response = await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { talla.Id },
            variants = new[] { VariantBody("CAM-S", ValueOf(talla, "S")), VariantBody("CAM-AZ", ValueOf(color, "Azul")) }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("catalog.option.value_not_found", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Variants[1].OptionValueIds[0]", out _));
    }

    // El mapa por campo del brief: el código que ya usa otro producto se marca en la variante que lo
    // trae, con el mismo código de error que el índice (catalog.product.code_taken).
    [Fact]
    public async Task ACodeTakenByAnotherProductIsMarkedOnTheVariant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        await PostProductAsync(client, TenantId, ProductBodies.Create("Vela de soja", "VS-001"));
        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M");

        var response = await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { talla.Id },
            variants = new[] { VariantBody("CAM-S", ValueOf(talla, "S")), VariantBody("VS-001", ValueOf(talla, "M")) }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("catalog.product.code_taken", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Variants[1].Code", out _));
    }

    // El listado sigue siendo por producto: cuenta las variantes y da el rango de precio de las
    // activas, sin traer el agregado.
    [Fact]
    public async Task ListShowsTheVariantCountAndThePriceRangeOfActiveVariants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M", "L");
        await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { talla.Id },
            variants = new object[]
            {
                VariantBody("CAM-S", 20m, ValueOf(talla, "S")),
                VariantBody("CAM-M", 30m, ValueOf(talla, "M")),
                new
                {
                    code = "CAM-L",
                    optionValueIds = new[] { ValueOf(talla, "L") },
                    pricing = ProductBodies.UsdPricing(99m),
                    isActive = false
                },
            }
        });

        var listed = Assert.Single((await ListAsync(client, TenantId)).Items);

        Assert.Equal(3, listed.VariantCount);
        Assert.Null(listed.Code);
        Assert.Equal(new PriceRangeResponse(20m, 30m), listed.PriceRange["USD"]);
    }

    private static object VariantBody(string code, params Guid[] values) => VariantBody(code, 10m, values);

    private static object VariantBody(string code, decimal usd, params Guid[] values) =>
        new { code, optionValueIds = values, pricing = ProductBodies.UsdPricing(usd) };

    private static Guid ValueOf(OptionResponse option, string name) =>
        option.Values.Single(value => value.Name == name).Id;

    private static async Task<OptionResponse> CreateOptionAsync(
        HttpClient client, string tenantId, string name, params string[] values)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/options",
            new { name, values },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var option = await response.Content.ReadFromJsonAsync<OptionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(option);
        return option;
    }

    private static Task<HttpResponseMessage> PostProductAsync(HttpClient client, string tenantId, object body) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products", body, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PutProductAsync(
        HttpClient client, string tenantId, Guid productId, object body) =>
        client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}", body, TestContext.Current.CancellationToken);

    private static async Task<ProductResponse> GetProductAsync(HttpClient client, string tenantId, Guid productId) =>
        await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}", TestContext.Current.CancellationToken));

    private static async Task<ProductsResponse> ListAsync(HttpClient client, string tenantId)
    {
        var body = await client.GetFromJsonAsync<ProductsResponse>(
            $"/api/v1/tenants/{tenantId}/catalog/products", TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
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

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var body = await ReadJsonAsync(response);
        return body.RootElement.GetProperty("code").GetString();
    }

    private static async Task<long> CountHistoryAsync(
        PostgreSqlContainer database, Guid productId, Guid? variantId = null)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            variantId is null
                ? "SELECT count(*) FROM catalog.product_price_changes WHERE product_id = @productId"
                : "SELECT count(*) FROM catalog.product_price_changes WHERE product_id = @productId AND variant_id = @variantId",
            connection);
        command.Parameters.AddWithValue("productId", productId);
        if (variantId is { } id)
        {
            command.Parameters.AddWithValue("variantId", id);
        }

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // Mismo atajo que ProductImageApiTests: la sesión de carga por la API y el estado Available por
    // SQL, porque el binario va contra R2, que en pruebas no existe.
    private static async Task<Guid> UploadImageAsync(
        HttpClient client, string tenantId, PostgreSqlContainer database, string name)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new { ownerId = Guid.CreateVersion7(), ownerType = "Product", name, mimeType = "image/png", sizeBytes = 2048 },
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        var fileId = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("fileResourceId").GetGuid();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE storage.file_resources SET status = 'Available' WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", fileId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        return fileId;
    }

    private static async Task PublishAsync(PostgreSqlContainer database, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE storage.file_resources SET public_storage_key = @key, published_at = now() WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", fileId);
        command.Parameters.AddWithValue("key", $"public/{fileId}.png");
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
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
        QepApiFactory factory, string subjectId, string tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        return client;
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
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
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}

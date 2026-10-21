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
/// Variantes, Tarea 14b (spec §6): <c>GET /catalog/products?includeVariants=true</c> trae, en cada
/// ítem, las variantes vendibles para que el selector de cotizaciones y pedidos agregue una línea
/// sin otra llamada. Sin el parámetro el listado queda como lo dejó la Tarea 5.
/// </summary>
public sealed class ProductSellableVariantsApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-0000000000e1";
    private const string SubjectId = "01900000-0000-7000-8000-0000000000e2";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000e3";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000e4";
    private const string PublicBaseUrl = "https://cdn.qep.test";

    private static readonly int[] TwelvePack = [12];

    private static readonly string[] All =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        StoragePermissions.FileRead,
        StoragePermissions.FileUpload
    ];

    // Una camiseta con tres tallas, creadas en el orden M, S, L: el selector las recibe en ese
    // orden de presentación (Position), no ordenadas por código ni por valor. Cada una trae lo que
    // la línea necesita: id, nombre visible armado, código, código de barras, precios en orden de
    // catálogo, escalas ordenadas con sus finales y los empaques de la variante.
    [Fact]
    public async Task ListWithVariantsReturnsSellableVariantsInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M", "L");
        var created = await ReadProductAsync(await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { talla.Id },
            variants = new object[]
            {
                new
                {
                    code = "CAM-M",
                    barcode = "7700000000011",
                    optionValueIds = new[] { ValueOf(talla, "M") },
                    pricing = new
                    {
                        prices = new Dictionary<string, decimal> { ["COP"] = 80000m, ["USD"] = 20m },
                        packagingUnits = TwelvePack,
                        scales = new object[]
                        {
                            new { fromUnit = 10, toUnit = 50, discount = 15m, restriction = "packaging_unit" },
                            new { fromUnit = 1, toUnit = 9, discount = 10m, restriction = "multiple", multiple = 3 },
                        }
                    }
                },
                VariantBody("CAM-S", 18m, ValueOf(talla, "S")),
                VariantBody("CAM-L", 22m, ValueOf(talla, "L")),
            }
        }));

        var list = await ListAsync(client, TenantId, "?includeVariants=true");

        var item = Assert.Single(list.Items);
        Assert.Equal(created.Id, item.Id);
        Assert.NotNull(item.Variants);
        Assert.Equal(["CAM-M", "CAM-S", "CAM-L"], item.Variants.Select(variant => variant.Code));
        Assert.Equal(
            ["Camiseta – M", "Camiseta – S", "Camiseta – L"],
            item.Variants.Select(variant => variant.Name));
        Assert.Equal(created.Variants.Select(variant => variant.Id), item.Variants.Select(variant => variant.Id));

        var medium = item.Variants[0];
        Assert.Equal("7700000000011", medium.Barcode);
        // Orden de catálogo (COP, USD), no el de inserción, igual que el detalle.
        Assert.Equal(["COP", "USD"], medium.Prices.Keys);
        Assert.Equal(20m, medium.Prices["USD"]);
        Assert.Equal(80000m, medium.Prices["COP"]);
        Assert.Equal([12], medium.PackagingUnits);
        // Ordenadas por FromUnit aunque se guardaron al revés, con los finales por moneda.
        Assert.Equal([1, 10], medium.PriceScales.Select(scale => scale.FromUnit));
        var multiple = medium.PriceScales.First();
        Assert.Equal(9, multiple.ToUnit);
        Assert.Equal(10m, multiple.Discount);
        Assert.Equal("multiple", multiple.Restriction);
        Assert.Equal(3, multiple.Multiple);
        Assert.Equal(18m, multiple.Finals["USD"]);
        Assert.Equal(72000m, multiple.Finals["COP"]);
        var packaging = medium.PriceScales.Last();
        Assert.Equal("packaging_unit", packaging.Restriction);
        Assert.Null(packaging.Multiple);

        // Las otras no tienen escalas ni empaques propios, y los campos viajan igual (vacíos).
        Assert.Null(item.Variants[1].Barcode);
        Assert.Empty(item.Variants[1].PriceScales);
        Assert.Empty(item.Variants[1].PackagingUnits);
        Assert.Equal(18m, item.Variants[1].Prices["USD"]);
        Assert.Equal(22m, item.Variants[2].Prices["USD"]);
    }

    // Un producto sin opciones trae su única variante, con el id del producto y el nombre del
    // producto: el selector no distingue entre «con variantes» y «sin ellas».
    [Fact]
    public async Task ListWithVariantsReturnsTheSingleVariantOfAProductWithoutOptions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var created = await ReadProductAsync(await PostProductAsync(
            client, TenantId, ProductBodies.Create("Vela de soja", "VS-001", ProductBodies.UsdPricing(12m))));

        var list = await ListAsync(client, TenantId, "?includeVariants=true");

        var item = Assert.Single(list.Items);
        Assert.Equal("VS-001", item.Code);
        var variant = Assert.Single(item.Variants!);
        Assert.Equal(created.Id, variant.Id);
        Assert.Equal("Vela de soja", variant.Name);
        Assert.Equal("VS-001", variant.Code);
        Assert.Equal(12m, variant.Prices["USD"]);
    }

    // Sin el parámetro, el ítem es el de la Tarea 5: variantCount y priceRange, sin `variants` ni
    // siquiera como null, y sin los campos que salieron del ítem (precios, escalas, empaques).
    [Fact]
    public async Task ListWithoutFlagKeepsTodayShape()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M");
        await ReadProductAsync(await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { talla.Id },
            variants = new object[]
            {
                VariantBody("CAM-S", 18m, ValueOf(talla, "S")),
                VariantBody("CAM-M", 22m, ValueOf(talla, "M")),
            }
        }));

        foreach (var query in new[] { string.Empty, "?includeVariants=false" })
        {
            var response = await client.GetAsync(
                $"/api/v1/tenants/{TenantId}/catalog/products{query}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray().ToArray());
            Assert.False(item.TryGetProperty("variants", out _));
            Assert.False(item.TryGetProperty("prices", out _));
            Assert.False(item.TryGetProperty("priceScales", out _));
            Assert.False(item.TryGetProperty("packagingUnits", out _));
            Assert.Equal(2, item.GetProperty("variantCount").GetInt32());
            var usd = item.GetProperty("priceRange").GetProperty("USD");
            Assert.Equal(18m, usd.GetProperty("min").GetDecimal());
            Assert.Equal(22m, usd.GetProperty("max").GetDecimal());
        }
    }

    // Una variante desactivada no se vende: el selector no la ve, pero el producto sí cuenta las
    // dos en variantCount (la grilla del catálogo no cambia).
    [Fact]
    public async Task ListWithVariantsHidesInactiveVariants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M", "L");
        await ReadProductAsync(await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { talla.Id },
            variants = new object[]
            {
                VariantBody("CAM-S", 18m, ValueOf(talla, "S")),
                new
                {
                    code = "CAM-M",
                    optionValueIds = new[] { ValueOf(talla, "M") },
                    pricing = ProductBodies.UsdPricing(20m),
                    isActive = false
                },
                VariantBody("CAM-L", 22m, ValueOf(talla, "L")),
            }
        }));

        var item = Assert.Single((await ListAsync(client, TenantId, "?includeVariants=true")).Items);

        Assert.Equal(3, item.VariantCount);
        Assert.Equal(["CAM-S", "CAM-L"], item.Variants!.Select(variant => variant.Code));
    }

    // Vendible es producto activo y variante activa: un producto dado de baja sigue en el listado
    // (el filtro isActive es del llamador) pero sin variantes que ofrecer.
    [Fact]
    public async Task ListWithVariantsGivesNoVariantsForAnInactiveProduct()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var active = await ReadProductAsync(await PostProductAsync(
            client, TenantId, ProductBodies.Create("Vela de soja", "VS-001")));
        var inactive = await ReadProductAsync(await PostProductAsync(
            client, TenantId, ProductBodies.Create("Vela de coco", "VC-001")));
        var deactivated = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{inactive.Id}/deactivate",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var list = await ListAsync(client, TenantId, "?includeVariants=true");

        Assert.Equal(2, list.Items.Count);
        Assert.Equal([active.Id], list.Items.Single(item => item.IsActive).Variants!.Select(variant => variant.Id));
        Assert.Empty(list.Items.Single(item => !item.IsActive).Variants!);
    }

    // La foto de la variante es la suya y, sin ella, la principal del producto: el selector no
    // cruza galería ni resuelve URLs.
    [Fact]
    public async Task ListWithVariantsReturnsTheVariantPhotoOrTheMainOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var main = await UploadImageAsync(client, TenantId, database, "principal.png");
        var blue = await UploadImageAsync(client, TenantId, database, "azul.png");
        await PublishAsync(database, main);
        await PublishAsync(database, blue);
        var color = await CreateOptionAsync(client, TenantId, "Color", "Azul", "Rojo");
        await ReadProductAsync(await PostProductAsync(client, TenantId, new
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
                VariantBody("CAM-RO", 10m, ValueOf(color, "Rojo")),
            }
        }));

        var item = Assert.Single((await ListAsync(client, TenantId, "?includeVariants=true")).Items);

        Assert.Equal($"{PublicBaseUrl}/public/{blue}.png", item.Variants![0].ImageUrl);
        Assert.Equal($"{PublicBaseUrl}/public/{main}.png", item.Variants[1].ImageUrl);
        Assert.Equal($"{PublicBaseUrl}/public/{main}.png", item.ImageUrl);
    }

    // El selector busca con el mismo `search` del listado: un producto que coincide por el código
    // de una de sus variantes llega con todas sus variantes vendibles, no sólo con la que coincidió.
    [Fact]
    public async Task ListWithVariantsAndSearchReturnsAllSellableVariantsOfTheMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M");
        await ReadProductAsync(await PostProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            optionIds = new[] { talla.Id },
            variants = new object[]
            {
                VariantBody("CAM-S", 18m, ValueOf(talla, "S")),
                VariantBody("CAM-M", 22m, ValueOf(talla, "M")),
            }
        }));
        await ReadProductAsync(await PostProductAsync(
            client, TenantId, ProductBodies.Create("Vela de soja", "VS-001")));

        var list = await ListAsync(client, TenantId, "?includeVariants=true&search=CAM-M");

        var item = Assert.Single(list.Items);
        Assert.Equal(["CAM-S", "CAM-M"], item.Variants!.Select(variant => variant.Code));
    }

    // Doble capa de tenant: la ruta es de otro tenant, así que es 403 y no un listado vacío ni un
    // 404. Tiene el permiso, para que el 403 sea atribuible al tenant.
    [Fact]
    public async Task ListWithVariantsForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, OtherSubjectId, OtherTenantId, CatalogPermissions.ProductRead);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products?includeVariants=true",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

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

    private static async Task<ProductsResponse> ListAsync(HttpClient client, string tenantId, string query)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products{query}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProductsResponse>(TestContext.Current.CancellationToken);
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

    // Mismo atajo que ProductVariantsApiTests: la sesión de carga por la API y el estado Available
    // por SQL, porque el binario va contra R2, que en pruebas no existe.
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

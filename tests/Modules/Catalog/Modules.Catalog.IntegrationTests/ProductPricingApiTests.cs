using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// CAT-09 — precio base/final en USD y COP, y escalas por cantidad.
///
/// Las reglas de negocio ya las cubren las unitarias de <c>ProductTests</c> contra el agregado
/// en memoria. Lo que este archivo verifica es lo que esas pruebas no pueden ver: que
/// <c>ProductRepository</c> de verdad traiga y reemplace las escalas contra Postgres. La
/// primera corrida manual encontró justo ese hueco — <c>FindAsync</c>/<c>SearchAsync</c> no
/// traían <c>PriceScales</c>, así que el `GET` volvía vacío y un `PUT` habría dejado las
/// escalas viejas huérfanas en la base en vez de reemplazarlas.
/// </summary>
public sealed class ProductPricingApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000041";
    private const string SubjectId = "01900000-0000-7000-8000-000000000042";

    // CAT-09 escalas requieren una lista de precios valida y activa (modulo pricing): las
    // pruebas necesitan tambien los permisos de Pricing para darse de alta una antes de crear
    // el producto.
    private static readonly string[] ManagePermissions =
    [
        CatalogPermissions.ProductRead, CatalogPermissions.ProductManage,
        "pricing.price_list.read", "pricing.price_list.manage"
    ];

    private static async Task<Guid> CreatePriceListIdAsync(
        HttpClient client, string tenantId, string name = "Mayorista", string prefix = "MAY")
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/pricing/price-lists",
            new { name, prefix },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"No se pudo crear la lista de precios de prueba: {(int)response.StatusCode} " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<PriceListDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    private sealed record PriceListDto(Guid Id);

    [Fact]
    public async Task CreateWithScalesPersistsAndGetReturnsThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var priceListId = await CreatePriceListIdAsync(client, TenantId);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            pricing = new
            {
                baseUsd = 100m,
                baseCop = 400000m,
                finalUsd = 90m,
                finalCop = 360000m,
                discount = 10m,
                scales = new object[]
                {
                    new
                    {
                        priceListId,
                        fromUnit = 1,
                        toUnit = 9,
                        discount = 5m,
                        restriction = "multiple",
                        multiple = 3,
                        finalUsd = 95m,
                        finalCop = 380000m
                    },
                    new
                    {
                        priceListId,
                        fromUnit = 10,
                        toUnit = 50,
                        discount = 15m,
                        restriction = "packaging_unit",
                        packagingUnit = 12,
                        finalUsd = 85m,
                        finalCop = 340000m
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.Equal(100m, created.PriceBaseUsd);
        Assert.Equal(2, created.PriceScales.Count);

        // Releído desde la base, no desde la respuesta de la escritura — eso probaría el
        // mapeo de salida, no si ProductRepository trae las escalas al leer.
        var fetched = await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            TestContext.Current.CancellationToken));

        Assert.Equal(2, fetched.PriceScales.Count);
        var multipleScale = Assert.Single(fetched.PriceScales, scale => scale.Restriction == "multiple");
        Assert.Equal(1, multipleScale.FromUnit);
        Assert.Equal(9, multipleScale.ToUnit);
        Assert.Equal(3, multipleScale.Multiple);
        Assert.Equal(95m, multipleScale.FinalUsd);

        var packagingScale = Assert.Single(
            fetched.PriceScales, scale => scale.Restriction == "packaging_unit");
        Assert.Equal(12, packagingScale.PackagingUnit);

        // El listado pasa por SearchAsync, un camino distinto de FindAsync — ambos necesitan
        // su propio Include.
        var list = await client.GetFromJsonAsync<ProductsResponse>(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            TestContext.Current.CancellationToken);
        Assert.NotNull(list);
        var listed = Assert.Single(list.Items);
        Assert.Equal(2, listed.PriceScales.Count);
    }

    // La prueba que hubiera encontrado el hueco de origen: sin el Include en FindAsync, las
    // escalas viejas nunca entran al change tracker, así que Clear() no las ve y el Update
    // sólo agrega la nueva encima — la base queda con las dos, no con una.
    [Fact]
    public async Task UpdateReplacesTheScalesInsteadOfAccumulatingThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var priceListId = await CreatePriceListIdAsync(client, TenantId);

        var created = await ReadProductAsync(await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            pricing = new
            {
                baseUsd = 100m,
                finalUsd = 100m,
                scales = new object[]
                {
                    new
                    {
                        priceListId,
                        fromUnit = 1,
                        toUnit = 9,
                        discount = 0m,
                        restriction = "multiple",
                        multiple = 3,
                        finalUsd = 100m
                    }
                }
            }
        }));
        Assert.Single(created.PriceScales);

        var updated = await ReadProductAsync(await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new
            {
                name = "Vela de soja",
                code = "VS-001",
                pricing = new
                {
                    baseUsd = 100m,
                    finalUsd = 100m,
                    scales = new object[]
                    {
                        new
                        {
                            priceListId,
                            fromUnit = 20,
                            toUnit = 40,
                            discount = 0m,
                            restriction = "packaging_unit",
                            packagingUnit = 6,
                            finalUsd = 100m
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken));

        var onlyScale = Assert.Single(updated.PriceScales);
        Assert.Equal(20, onlyScale.FromUnit);
        Assert.Equal(6, onlyScale.PackagingUnit);

        // Releído de la base: si el Update hubiera dejado la escala vieja huérfana, esto
        // volvería con dos filas en vez de una.
        var fetched = await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            TestContext.Current.CancellationToken));
        Assert.Single(fetched.PriceScales);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM catalog.product_price_scales WHERE product_id = @id", connection);
        command.Parameters.AddWithValue("id", created.Id);
        var count = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreateWithAnUnknownPriceListIdReturnsUnprocessableEntity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            pricing = new
            {
                baseUsd = 100m,
                finalUsd = 100m,
                scales = new object[]
                {
                    new
                    {
                        priceListId = Guid.NewGuid(),
                        fromUnit = 1,
                        toUnit = 9,
                        discount = 0m,
                        restriction = "multiple",
                        multiple = 3,
                        finalUsd = 100m
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "catalog.product.price_scale.price_list_not_found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithAnInactivePriceListIdReturnsUnprocessableEntity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var priceListId = await CreatePriceListIdAsync(client, TenantId);
        await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/pricing/price-lists/{priceListId}/deactivate",
            null,
            TestContext.Current.CancellationToken);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            pricing = new
            {
                baseUsd = 100m,
                finalUsd = 100m,
                scales = new object[]
                {
                    new
                    {
                        priceListId,
                        fromUnit = 1,
                        toUnit = 9,
                        discount = 0m,
                        restriction = "multiple",
                        multiple = 3,
                        finalUsd = 100m
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "catalog.product.price_scale.price_list_inactive", body, StringComparison.Ordinal);
    }

    // La regla vive en el dominio (ProductTests ya la cubre en memoria); esta prueba confirma
    // que el camino completo — validador de forma, resolución de la lista, dominio — deja pasar
    // el 422 con el código correcto hasta la respuesta HTTP.
    [Fact]
    public async Task CreateWithOverlappingScalesInTheSamePriceListReturnsUnprocessableEntity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var priceListId = await CreatePriceListIdAsync(client, TenantId);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            code = "CAM-001",
            pricing = new
            {
                baseUsd = 10m,
                finalUsd = 10m,
                scales = new object[]
                {
                    new
                    {
                        priceListId,
                        fromUnit = 1,
                        toUnit = 9,
                        discount = 0m,
                        restriction = "multiple",
                        multiple = 3,
                        finalUsd = 10m
                    },
                    new
                    {
                        priceListId,
                        fromUnit = 5,
                        toUnit = 20,
                        discount = 0m,
                        restriction = "multiple",
                        multiple = 3,
                        finalUsd = 10m
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "catalog.product.price_scale.range_overlap", body, StringComparison.Ordinal);
    }

    // Mismos rangos, listas distintas: no es solapamiento, es exactamente el caso de uso
    // (Mayorista y VIP con la misma segmentación de cantidades, descuentos distintos).
    [Fact]
    public async Task CreateWithTheSameRangeInDifferentPriceListsSucceeds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var wholesale = await CreatePriceListIdAsync(client, TenantId, "Mayorista", "MAY");
        var vip = await CreatePriceListIdAsync(client, TenantId, "VIP", "VIP");

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Camiseta",
            code = "CAM-001",
            pricing = new
            {
                baseUsd = 10m,
                finalUsd = 10m,
                scales = new object[]
                {
                    new
                    {
                        priceListId = wholesale,
                        fromUnit = 1,
                        toUnit = 9,
                        discount = 0m,
                        restriction = "multiple",
                        multiple = 3,
                        finalUsd = 10m
                    },
                    new
                    {
                        priceListId = vip,
                        fromUnit = 1,
                        toUnit = 9,
                        discount = 10m,
                        restriction = "multiple",
                        multiple = 3,
                        finalUsd = 9m
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.Equal(2, created.PriceScales.Count);
    }

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
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}

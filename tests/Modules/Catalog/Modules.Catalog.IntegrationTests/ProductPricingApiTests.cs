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

    // Los empaques viajan en el precio del producto desde el 2026-10-01, no dentro de la escala.
    private static readonly int[] TwelvePack = [12];
    private static readonly int[] SixPack = [6];

    private static readonly string[] ManagePermissions =
    [
        CatalogPermissions.ProductRead, CatalogPermissions.ProductManage
    ];

    [Fact]
    public async Task CreateWithScalesPersistsAndGetReturnsThem()
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
                baseCop = 400000m,
                packagingUnits = TwelvePack,
                scales = new object[]
                {
                    new
                    {
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
                        fromUnit = 10,
                        toUnit = 50,
                        discount = 15m,
                        restriction = "packaging_unit",
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

        Assert.Single(fetched.PriceScales, scale => scale.Restriction == "packaging_unit");
        // El empaque es del producto, no de la escala (2026-10-01).
        Assert.Equal([12], fetched.PackagingUnits);

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

        var created = await ReadProductAsync(await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-001",
            pricing = new
            {
                baseUsd = 100m,
                scales = new object[]
                {
                    new
                    {
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
                    packagingUnits = SixPack,
                    scales = new object[]
                    {
                        new
                        {
                            fromUnit = 20,
                            toUnit = 40,
                            discount = 0m,
                            restriction = "packaging_unit",
                            finalUsd = 100m
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken));

        var onlyScale = Assert.Single(updated.PriceScales);
        Assert.Equal(20, onlyScale.FromUnit);
        Assert.Equal([6], updated.PackagingUnits);

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

    // El flag viaja de ida y de vuelta: sin el de vuelta, la pantalla de producto no puede
    // dibujar el switch en el estado en que quedó guardado.
    [Fact]
    public async Task CreateProductRoundTripsTheGroupingFlagOnAMultipleScale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-AGR-001",
            pricing = new
            {
                baseCop = 100_000m,
                scales = new object[]
                {
                    new
                    {
                        fromUnit = 5, toUnit = 48, discount = 5m,
                        restriction = "multiple", multiple = 3,
                        finalCop = 95_000m, allowGrouping = true
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.True(Assert.Single(created.PriceScales).AllowGrouping);
    }

    [Fact]
    public async Task CreateProductRejectsGroupingOnAPackagingUnitScale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Vela de soja",
            code = "VS-AGR-002",
            pricing = new
            {
                baseCop = 100_000m,
                packagingUnits = TwelvePack,
                scales = new object[]
                {
                    new
                    {
                        fromUnit = 1, toUnit = 999, discount = 0m,
                        restriction = "packaging_unit",
                        finalCop = 100_000m, allowGrouping = true
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("catalog.product.price_scale.grouping_not_allowed", problem.Code);
    }

    // ---- Empaques del producto (2026-10-01) ----

    private static readonly int[] DuplicatedPackagingUnits = [100, 100];
    private static readonly int[] UnsortedPackagingUnits = [150, 100];

    // Una escala packaging_unit ya no trae número: sin empaques en el producto, el dominio la
    // rechaza con su propio código y el formulario sabe qué campo pedir.
    [Fact]
    public async Task CreateProductRejectsAPackagingScaleWithoutPackagingUnits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Keratina 120 ml",
            code = "KR-120",
            pricing = new
            {
                baseCop = 100_000m,
                scales = new object[]
                {
                    new
                    {
                        fromUnit = 100, toUnit = 999, discount = 0m,
                        restriction = "packaging_unit", finalCop = 100_000m
                    }
                }
            }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("catalog.product.packaging_units_required", problem.Code);
    }

    [Fact]
    public async Task CreateProductRejectsDuplicatedPackagingUnits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateProductAsync(client, TenantId, new
        {
            name = "Keratina 120 ml",
            code = "KR-120",
            pricing = new { baseCop = 100_000m, packagingUnits = DuplicatedPackagingUnits }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("catalog.product.packaging_units.duplicated", problem.Code);
    }

    // Ida y vuelta contra Postgres: el integer[] se guarda ordenado, vuelve igual en el GET y en
    // el listado, y un PUT sin empaques lo deja vacío — vacío y no ausente, porque la pantalla
    // repinta lo que llega.
    [Fact]
    public async Task PackagingUnitsRoundTripThroughTheDatabase()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(await CreateProductAsync(client, TenantId, new
        {
            name = "Keratina 120 ml",
            code = "KR-120",
            pricing = new
            {
                baseCop = 100_000m,
                packagingUnits = UnsortedPackagingUnits,
                scales = new object[]
                {
                    new
                    {
                        fromUnit = 100, toUnit = 999, discount = 0m,
                        restriction = "packaging_unit", finalCop = 100_000m
                    }
                }
            }
        }));
        Assert.Equal([100, 150], created.PackagingUnits);

        var fetched = await ReadProductAsync(await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            TestContext.Current.CancellationToken));
        Assert.Equal([100, 150], fetched.PackagingUnits);

        var list = await client.GetFromJsonAsync<ProductsResponse>(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            TestContext.Current.CancellationToken);
        Assert.NotNull(list);
        Assert.Equal([100, 150], Assert.Single(list.Items).PackagingUnits);

        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT packaging_units FROM catalog.products WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", created.Id);
            var stored = (int[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal([100, 150], stored);
        }

        var cleared = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new
            {
                name = "Keratina 120 ml",
                code = "KR-120",
                pricing = new { baseCop = 100_000m }
            },
            TestContext.Current.CancellationToken);
        var body = await cleared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(cleared.IsSuccessStatusCode, body);
        Assert.Contains("\"packagingUnits\":[]", body, StringComparison.Ordinal);
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
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }

    /// <summary>Las extensiones de ProblemDetails llegan aplanadas en la raiz
    /// (ApiExceptionHandler).</summary>
    private sealed record ProblemPayload(string Code);
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// El histórico de cambios de precio de un producto.
///
/// Las reglas de qué cuenta como cambio ya las cubren las unitarias de
/// <c>ProductPriceChangeDetectorTests</c> contra el detector en memoria. Lo que este archivo
/// verifica es lo que ésas no pueden ver: que la fila **llegue a Postgres**, con el sujeto y el
/// instante correctos, y que lo haga en la misma transacción que el producto — un `PUT` que
/// falla al commitear no puede dejar histórico de un cambio que no ocurrió.
/// </summary>
public sealed class ProductPriceHistoryApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000091";
    private const string SubjectId = "01900000-0000-7000-8000-000000000092";

    private static readonly string[] ManagePermissions =
    [
        CatalogPermissions.ProductRead, CatalogPermissions.ProductManage
    ];

    [Fact]
    public async Task UpdatingThePriceWritesTheHistoryRowsWithTheAuthorAndTheInstant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(await CreateProductAsync(client, "VS-001", new
        {
            prices = new Dictionary<string, decimal> { ["USD"] = 100m, ["COP"] = 400000m },
            scales = new object[]
            {
                new
                {
                    fromUnit = 1,
                    toUnit = 9,
                    discount = 10m,
                    restriction = "multiple",
                    multiple = 3
                }
            }
        }));

        // Crear no deja histórico: no hay un "antes" del que se haya cambiado nada.
        Assert.Empty(await ReadHistoryAsync(database, created.Id));

        var before = DateTimeOffset.UtcNow;
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            ProductBodies.Update(
                created,
                "Vela de soja",
                "VS-001",
                new
                {
                    prices = new Dictionary<string, decimal> { ["USD"] = 120m, ["COP"] = 400000m },
                    scales = new object[]
                    {
                        new
                        {
                            fromUnit = 1,
                            toUnit = 9,
                            discount = 25m,
                            restriction = "multiple",
                            multiple = 3
                        }
                    }
                }),
            TestContext.Current.CancellationToken);
        await ReadProductAsync(response);
        var after = DateTimeOffset.UtcNow;

        var history = await ReadHistoryAsync(database, created.Id);
        Assert.Equal(2, history.Count);

        // El precio en COP no se tocó, así que no tiene fila: el histórico registra cambios, no
        // guardados.
        Assert.DoesNotContain(history, row => row.Field == "PriceBase" && row.Currency == "COP");

        var baseUsd = Assert.Single(history, row => row.Field == "PriceBase" && row.Currency == "USD");
        Assert.Equal(100m, baseUsd.PreviousValue);
        Assert.Equal(120m, baseUsd.NewValue);
        Assert.Null(baseUsd.ScaleFromUnit);
        Assert.Null(baseUsd.ScaleToUnit);

        var scaleDiscount = Assert.Single(history, row => row.Field == "ScaleDiscount");
        Assert.Equal(10m, scaleDiscount.PreviousValue);
        Assert.Equal(25m, scaleDiscount.NewValue);
        Assert.Equal(1, scaleDiscount.ScaleFromUnit);
        Assert.Equal(9, scaleDiscount.ScaleToUnit);

        Assert.All(history, row =>
        {
            Assert.Equal(Guid.Parse(TenantId), row.TenantId);
            Assert.Equal(Guid.Parse(SubjectId), row.ChangedBy);
            Assert.InRange(row.ChangedAt, before, after);
            // Tarea 7: cada fila lleva la variante cuyo precio cambió (la única, que tiene el id del producto).
            Assert.Equal(created.Only().Id, row.VariantId);
        });
    }

    /// <summary>
    /// Misma transacción que el producto, no un guardado aparte. El `PUT` de acá cambia el
    /// precio **y** pisa el código de otro producto: el rechazo (desde la Tarea 5 lo da la
    /// comprobación previa del handler con el campo marcado, y en una carrera el índice
    /// <c>IX_product_variants_tenant_code</c>) tira el guardado entero, y con él tiene que irse el
    /// histórico. Con un
    /// <c>SaveChangesAsync</c> propio para las filas de cambio esta prueba encuentra un
    /// histórico que afirma un cambio de precio que el catálogo nunca tuvo.
    /// </summary>
    [Fact]
    public async Task AFailedUpdateLeavesNoHistoryBehind()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        await ReadProductAsync(await CreateProductAsync(client, "VS-001", new { prices = new Dictionary<string, decimal> { ["USD"] = 100m } }));
        var target = await ReadProductAsync(
            await CreateProductAsync(client, "VS-002", new { prices = new Dictionary<string, decimal> { ["USD"] = 100m } }));

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{target.Id}",
            ProductBodies.Update(
                target,
                "Vela de soja",
                "VS-001",
                new { prices = new Dictionary<string, decimal> { ["USD"] = 555m } }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await ReadHistoryAsync(database, target.Id));
    }

    // Tarea 7. El PUT de una camiseta con Talla S y M cambia el precio sólo de M: la historia lleva
    // una fila, con el VariantId de M y su precio de antes; S, que llegó igual, no deja ninguna.
    [Fact]
    public async Task UpdatingOneVariantWritesHistoryOnlyForThatVariant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var talla = await CreateTallaAsync(client);
        var shirt = await CreateShirtAsync(client, talla);
        var (small, medium) = (shirt.Variants[0], shirt.Variants[1]);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{shirt.Id}",
            new
            {
                expectedVersion = shirt.Version,
                name = "Camiseta",
                optionIds = new[] { talla.Id },
                variants = new object[]
                {
                    new { id = small.Id, code = "CAM-S", pricing = ProductBodies.UsdPricing(20m) },
                    new { id = medium.Id, code = "CAM-M", pricing = ProductBodies.UsdPricing(35m) },
                }
            },
            TestContext.Current.CancellationToken);
        await ReadProductAsync(response);

        var row = Assert.Single(await ReadHistoryAsync(database, shirt.Id));
        Assert.Equal(medium.Id, row.VariantId);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(((decimal?)30m, (decimal?)35m), (row.PreviousValue, row.NewValue));
    }

    // Tarea 7. Copiar escalas escribe historia por variante destino: la que ya tenía las escalas
    // del origen no deja filas; la que estaba sin escalas deja una por escala nueva, con su VariantId.
    [Fact]
    public async Task CopyingScalesWritesHistoryPerTargetVariant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var scales = new object[]
        {
            new { fromUnit = 1, toUnit = 9, discount = 10m, restriction = "multiple", multiple = 3 },
            new { fromUnit = 10, toUnit = 50, discount = 20m, restriction = "multiple", multiple = 5 },
        };
        var source = await ReadProductAsync(await CreateProductAsync(client, "VS-001", new { prices = new Dictionary<string, decimal> { ["USD"] = 100m }, scales }));
        var alreadyEqual = await ReadProductAsync(await CreateProductAsync(client, "VS-002", new { prices = new Dictionary<string, decimal> { ["USD"] = 80m }, scales }));
        var bare = await ReadProductAsync(await CreateProductAsync(client, "VS-003", new { prices = new Dictionary<string, decimal> { ["USD"] = 60m } }));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/price-scales/copy",
            new { sourceVariantId = source.Only().Id, targetVariantIds = new[] { alreadyEqual.Only().Id, bare.Only().Id } },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await ReadHistoryAsync(database, alreadyEqual.Id));
        var rows = await ReadHistoryAsync(database, bare.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("ScaleDiscount", row.Field);
            Assert.Equal(bare.Only().Id, row.VariantId);
            Assert.Null(row.PreviousValue);
            Assert.Equal(Guid.Parse(SubjectId), row.ChangedBy);
        });
        Assert.Equal([10m, 20m], rows.OrderBy(row => row.ScaleFromUnit).Select(row => row.NewValue!.Value));
        // El origen no es destino: no deja historia.
        Assert.Empty(await ReadHistoryAsync(database, source.Id));
    }

    private static readonly string[] TallaValues = ["S", "M"];

    private static async Task<OptionResponse> CreateTallaAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/options",
            new { name = "Talla", values = TallaValues },
            TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var option = await response.Content.ReadFromJsonAsync<OptionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(option);
        return option;
    }

    private static async Task<ProductResponse> CreateShirtAsync(HttpClient client, OptionResponse talla)
    {
        object VariantOf(string code, string size, decimal usd) => new
        {
            code,
            optionValueIds = new[] { talla.Values.Single(value => value.Name == size).Id },
            pricing = ProductBodies.UsdPricing(usd)
        };

        return await ReadProductAsync(await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name = "Camiseta",
                optionIds = new[] { talla.Id },
                variants = new[] { VariantOf("CAM-S", "S", 20m), VariantOf("CAM-M", "M", 30m) }
            },
            TestContext.Current.CancellationToken));
    }

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client,
        string code,
        object pricing) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            ProductBodies.Create("Vela de soja", code, pricing),
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

    // Leído por SQL y no por un endpoint: no hay ninguno todavía —el reporte es otro trabajo— y
    // la pregunta acá es justamente si la fila está en la tabla.
    private static async Task<IReadOnlyList<HistoryRow>> ReadHistoryAsync(
        PostgreSqlContainer database,
        Guid productId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT tenant_id, field, scale_from_unit, scale_to_unit,
                   previous_value, new_value, changed_by, changed_at, currency, variant_id
            FROM catalog.product_price_changes
            WHERE product_id = @id
            ORDER BY changed_at, field
            """,
            connection);
        command.Parameters.AddWithValue("id", productId);

        var rows = new List<HistoryRow>();
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new HistoryRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.GetGuid(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetGuid(9)));
        }

        return rows;
    }

    private sealed record HistoryRow(
        Guid TenantId,
        string Field,
        int? ScaleFromUnit,
        int? ScaleToUnit,
        decimal? PreviousValue,
        decimal? NewValue,
        Guid ChangedBy,
        DateTimeOffset ChangedAt,
        string? Currency,
        Guid VariantId);

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
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}

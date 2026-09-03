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
            baseUsd = 100m,
            baseCop = 400000m,
            scales = new object[]
            {
                new
                {
                    fromUnit = 1,
                    toUnit = 9,
                    discount = 10m,
                    restriction = "multiple",
                    multiple = 3,
                    finalUsd = 90m,
                    finalCop = 360000m
                }
            }
        }));

        // Crear no deja histórico: no hay un "antes" del que se haya cambiado nada.
        Assert.Empty(await ReadHistoryAsync(database, created.Id));

        var before = DateTimeOffset.UtcNow;
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new
            {
                name = "Vela de soja",
                code = "VS-001",
                pricing = new
                {
                    baseUsd = 120m,
                    baseCop = 400000m,
                    scales = new object[]
                    {
                        new
                        {
                            fromUnit = 1,
                            toUnit = 9,
                            discount = 25m,
                            restriction = "multiple",
                            multiple = 3,
                            finalUsd = 90m,
                            finalCop = 300000m
                        }
                    }
                }
            },
            TestContext.Current.CancellationToken);
        await ReadProductAsync(response);
        var after = DateTimeOffset.UtcNow;

        var history = await ReadHistoryAsync(database, created.Id);
        Assert.Equal(2, history.Count);

        // El precio en COP no se tocó, así que no tiene fila: el histórico registra cambios, no
        // guardados.
        Assert.DoesNotContain(history, row => row.Field == "PriceBaseCop");

        var baseUsd = Assert.Single(history, row => row.Field == "PriceBaseUsd");
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
        });
    }

    /// <summary>
    /// Misma transacción que el producto, no un guardado aparte. El `PUT` de acá cambia el
    /// precio **y** pisa el código de otro producto: la violación de <c>IX_products_tenant_code</c>
    /// tira el commit entero, y con él tiene que irse el histórico. Con un
    /// <c>SaveChangesAsync</c> propio para las filas de cambio esta prueba encuentra un
    /// histórico que afirma un cambio de precio que el catálogo nunca tuvo.
    /// </summary>
    [Fact]
    public async Task AFailedUpdateLeavesNoHistoryBehind()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        await ReadProductAsync(await CreateProductAsync(client, "VS-001", new { baseUsd = 100m }));
        var target = await ReadProductAsync(
            await CreateProductAsync(client, "VS-002", new { baseUsd = 100m }));

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{target.Id}",
            new
            {
                name = "Vela de soja",
                code = "VS-001",
                pricing = new { baseUsd = 555m }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await ReadHistoryAsync(database, target.Id));
    }

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client,
        string code,
        object pricing) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new { name = "Vela de soja", code, pricing },
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
                   previous_value, new_value, changed_by, changed_at
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
                reader.GetFieldValue<DateTimeOffset>(7)));
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
        DateTimeOffset ChangedAt);

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
        }
    }
}

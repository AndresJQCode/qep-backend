using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// La exportacion del catalogo: genera el Excel, lo sube a la carpeta temporal de R2 y encola el
/// correo con el enlace prefirmado. Mismo flujo que <c>CustomerExportApiTests</c>.
///
/// El puerto de subida se reemplaza por un doble que captura los bytes — no hay bucket en las
/// pruebas, y ademas es la unica forma de abrir el workbook generado. Verificar solo el status HTTP
/// dejaria pasar un archivo vacio o con las columnas corridas, que es justo lo que este export
/// tiene de particular: las escalas son columnas dinamicas.
/// </summary>
public sealed class ProductExportApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000081";
    private const string SubjectId = "01900000-0000-7000-8000-000000000082";

    private static readonly string[] ManagePermissions =
    [
        CatalogPermissions.ProductRead, CatalogPermissions.ProductManage
    ];

    private static string ExportUrl() =>
        $"/api/v1/tenants/{TenantId}/catalog/products/export";

    /// <summary>
    /// Lo que hace distinto a este export: cada escala del catalogo es una columna, una escala
    /// compartida no se duplica, y el producto que no la tiene deja la celda vacia.
    /// </summary>
    [Fact]
    public async Task ExportPivotsPriceScalesIntoSharedColumns()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = new QepApiFactory(database.GetConnectionString(), storage);
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        // A: 1-9 y 10-19. B: 1-9 (compartida con A) y 20-99. C: sin escalas.
        // Los descuentos sobre BaseCop = 50.000 dan 45.000 / 42.500 / 30.000 / 25.000.
        await CreateProductAsync(client, "AAA-1", "Vela de soja", new object[]
        {
            Scale(1, 9, 10m),
            Scale(10, 19, 15m),
        });
        await CreateProductAsync(client, "BBB-2", "Difusor de madera", new object[]
        {
            Scale(1, 9, 40m),
            Scale(20, 99, 50m),
        });
        await CreateProductAsync(client, "CCC-3", "Jabon artesanal", []);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(3, body.ProductCount);
        Assert.EndsWith(".xlsx", body.FileName, StringComparison.Ordinal);

        Assert.NotNull(storage.Content);
        using var workbook = new XLWorkbook(new MemoryStream(storage.Content));
        var sheet = workbook.Worksheets.First();

        // Tres columnas de escala, ordenadas por unidad de inicio: 1-9 aparece una sola vez
        // aunque la compartan dos productos.
        var scaleHeaders = new[]
        {
            sheet.Cell(1, 8).GetString(),
            sheet.Cell(1, 9).GetString(),
            sheet.Cell(1, 10).GetString(),
        };
        Assert.Equal(["1-9", "10-19", "20-99"], scaleHeaders);
        Assert.Empty(sheet.Cell(1, 11).GetString());

        // Las filas salen ordenadas por codigo: AAA-1, BBB-2, CCC-3.
        Assert.Equal("AAA-1", sheet.Cell(2, 1).GetString());
        Assert.Equal("BBB-2", sheet.Cell(3, 1).GetString());
        Assert.Equal("CCC-3", sheet.Cell(4, 1).GetString());

        // A tiene 1-9 y 10-19, y deja 20-99 vacia.
        Assert.Equal(45_000m, sheet.Cell(2, 8).GetValue<decimal>());
        Assert.Equal(42_500m, sheet.Cell(2, 9).GetValue<decimal>());
        Assert.True(sheet.Cell(2, 10).IsEmpty());

        // B comparte la columna 1-9 con A y deja 10-19 vacia.
        Assert.Equal(30_000m, sheet.Cell(3, 8).GetValue<decimal>());
        Assert.True(sheet.Cell(3, 9).IsEmpty());
        Assert.Equal(25_000m, sheet.Cell(3, 10).GetValue<decimal>());

        // C no tiene ninguna escala: las tres celdas vacias, no ceros.
        Assert.True(sheet.Cell(4, 8).IsEmpty());
        Assert.True(sheet.Cell(4, 9).IsEmpty());
        Assert.True(sheet.Cell(4, 10).IsEmpty());

        // El correo no se manda en el request: queda encolado como evento de integracion.
        var events = await OutboxEventNamesAsync(database.GetConnectionString());
        Assert.Contains("catalog.product-export-ready.v1", events);
    }

    [Fact]
    public async Task ExportWithoutReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), new CapturingExportStorage());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CatalogPermissions.ProductManage);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Un tenant sin productos no produce un Excel de una sola fila de cabeceras ni un correo con
    // un archivo vacio: falla legible, mismo criterio que el export de clientes.
    [Fact]
    public async Task ExportWithoutProductsIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = new QepApiFactory(database.GetConnectionString(), storage);
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null(storage.Content);
    }

    /// <summary>Precio base comun a los tres productos, para que el precio final de cada escala
    /// se derive del descuento y el backend lo acepte: valida que final = base x (1 - desc/100).
    /// </summary>
    private const decimal BaseCop = 50_000m;

    private static object Scale(int fromUnit, int toUnit, decimal discount) => new
    {
        fromUnit,
        toUnit,
        discount,
        restriction = "multiple",
        multiple = 1,
        finalCop = BaseCop * (1m - discount / 100m),
    };

    private static async Task CreateProductAsync(
        HttpClient client, string code, string name, object[] scales)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name,
                code,
                pricing = new { baseCop = BaseCop, scales },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<List<string>> OutboxEventNamesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT event_name FROM platform.outbox_messages", connection);
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);

        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
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

    private sealed record ExportResponse(
        string FileName, int ProductCount, DateTimeOffset ExpiresAt);

    /// <summary>Captura los bytes en vez de subirlos: no hay bucket en las pruebas, y es la unica
    /// forma de abrir el workbook generado.</summary>
    private sealed class CapturingExportStorage : IProductExportStorage
    {
        public byte[]? Content { get; private set; }

        public Task<ProductExportUpload> UploadAsync(
            Guid tenantId, string fileName, byte[] content, CancellationToken cancellationToken)
        {
            Content = content;
            return Task.FromResult(new ProductExportUpload(
                "https://example.invalid/export.xlsx", DateTimeOffset.UtcNow.AddHours(24)));
        }
    }

    private sealed class QepApiFactory(
        string connectionString, IProductExportStorage exportStorage)
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
            builder.ConfigureServices(services =>
                services.AddScoped(_ => exportStorage));
        }
    }
}

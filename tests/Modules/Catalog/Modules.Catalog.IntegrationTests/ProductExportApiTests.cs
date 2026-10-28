using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Catalog.Application;
using Modules.Tenancy.Infrastructure.Persistence;
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

    private static readonly string[] ExpectedHeaders =
        ["Código", "Código de barras", "Producto", "Variante", "Descripción", "Estado", "Tasa de impuesto", "Precio base COP"];

    private static readonly string[] SizeValues = ["S", "M", "L"];

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
        await SeedTenantAsync(factory);

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

        // Seven fixed columns (code, barcode, product, variant, description, status, tax rate), then
        // one base price per currency, so the COP price is column 8. Header per scale (discount,
        // then the final in each currency in use), ordered by from unit: 1-9 appears once even
        // though two products share it. The products are COP-only.
        Assert.Equal("Precio base COP", sheet.Cell(1, 8).GetString());
        Assert.Equal(
            [
                "Descuento 1-9 (%)", "Final 1-9 COP",
                "Descuento 10-19 (%)", "Final 10-19 COP",
                "Descuento 20-99 (%)", "Final 20-99 COP",
            ],
            Enumerable.Range(9, 6).Select(column => sheet.Cell(1, column).GetString()).ToArray());
        Assert.Empty(sheet.Cell(1, 15).GetString());

        // Rows are ordered by code: AAA-1, BBB-2, CCC-3.
        Assert.Equal("AAA-1", sheet.Cell(2, 1).GetString());
        Assert.Equal("BBB-2", sheet.Cell(3, 1).GetString());
        Assert.Equal("CCC-3", sheet.Cell(4, 1).GetString());

        // A has 1-9 and 10-19, and leaves 20-99 empty.
        Assert.Equal(10m, sheet.Cell(2, 9).GetValue<decimal>());
        Assert.Equal(45_000m, sheet.Cell(2, 10).GetValue<decimal>());
        Assert.Equal(15m, sheet.Cell(2, 11).GetValue<decimal>());
        Assert.Equal(42_500m, sheet.Cell(2, 12).GetValue<decimal>());
        Assert.True(sheet.Cell(2, 13).IsEmpty());
        Assert.True(sheet.Cell(2, 14).IsEmpty());

        // B shares 1-9 with A, leaves 10-19 empty and has 20-99.
        Assert.Equal(40m, sheet.Cell(3, 9).GetValue<decimal>());
        Assert.Equal(30_000m, sheet.Cell(3, 10).GetValue<decimal>());
        Assert.True(sheet.Cell(3, 11).IsEmpty());
        Assert.True(sheet.Cell(3, 12).IsEmpty());
        Assert.Equal(50m, sheet.Cell(3, 13).GetValue<decimal>());
        Assert.Equal(25_000m, sheet.Cell(3, 14).GetValue<decimal>());

        // C has no scale: every scale cell empty, not zeros.
        for (var column = 9; column <= 14; column++)
        {
            Assert.True(sheet.Cell(4, column).IsEmpty());
        }

        // El correo no se manda en el request: queda encolado como evento de integracion.
        var events = await OutboxEventNamesAsync(database.GetConnectionString());
        Assert.Contains("catalog.product-export-ready.v1", events);
    }

    /// <summary>
    /// Variantes, Tarea 8: una fila por variante, con el nombre visible armado desde la biblioteca de
    /// opciones. Camiseta tiene tres variantes (la tercera, inactiva) que se crean en el orden S, M, L
    /// —no el alfabetico de sus codigos—; Vela es un producto sin opciones. Cubre el orden (producto
    /// por su codigo menor, variantes por posicion), el codigo de barras, la columna Variante vacia
    /// en la variante por defecto, el estado por variante y que `productCount` cuenta productos.
    /// </summary>
    [Fact]
    public async Task ExportWritesOneRowPerVariantWithVisibleName()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = new QepApiFactory(database.GetConnectionString(), storage);
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        await SeedTenantAsync(factory);

        var optionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/options",
            new { name = "Talla", values = SizeValues },
            TestContext.Current.CancellationToken);
        optionResponse.EnsureSuccessStatusCode();
        var talla = await optionResponse.Content.ReadFromJsonAsync<OptionResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(talla);

        object Variant(string code, string? barcode, string size, decimal cop, bool isActive = true) => new
        {
            code,
            barcode,
            optionValueIds = new[] { talla.Values.Single(value => value.Name == size).Id },
            pricing = new { prices = new Dictionary<string, decimal> { ["COP"] = cop } },
            isActive,
        };

        var camiseta = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name = "Camiseta básica",
                description = "Algodón",
                optionIds = new[] { talla.Id },
                variants = new[]
                {
                    Variant("CAM-S", "7701234567890", "S", 40_000m),
                    Variant("CAM-M", null, "M", 45_000m),
                    Variant("CAM-L", null, "L", 50_000m, isActive: false),
                },
            },
            TestContext.Current.CancellationToken);
        camiseta.EnsureSuccessStatusCode();
        await CreateProductAsync(client, "VS-001", "Vela de soja", []);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        // Dos productos, cuatro filas: el correo dice "2 productos".
        Assert.Equal(2, body.ProductCount);

        Assert.NotNull(storage.Content);
        using var workbook = new XLWorkbook(new MemoryStream(storage.Content));
        var sheet = workbook.Worksheets.First();

        Assert.Equal(
            ExpectedHeaders,
            Enumerable.Range(1, 8).Select(column => sheet.Cell(1, column).GetString()).ToArray());
        Assert.Equal(5, sheet.LastRowUsed()!.RowNumber());

        string[] RowOf(int row) =>
            Enumerable.Range(1, 6).Select(column => sheet.Cell(row, column).GetString()).ToArray();

        // Camiseta primero (su codigo menor, CAM-L, va antes que VS-001) y sus variantes en el orden
        // en que se crearon (S, M, L), no en el de sus codigos.
        Assert.Equal(
            ["CAM-S", "7701234567890", "Camiseta básica", "Camiseta básica – S", "Algodón", "Activo"],
            RowOf(2));
        Assert.Equal(
            ["CAM-M", "", "Camiseta básica", "Camiseta básica – M", "Algodón", "Activo"],
            RowOf(3));
        // La variante inactiva tambien se exporta, marcada.
        Assert.Equal(
            ["CAM-L", "", "Camiseta básica", "Camiseta básica – L", "Algodón", "Inactivo"],
            RowOf(4));
        // La variante por defecto no repite el nombre: Variante queda vacia.
        Assert.Equal(["VS-001", "", "Vela de soja", "", "", "Activo"], RowOf(5));

        // El precio es de la variante, no del producto.
        Assert.Equal(40_000m, sheet.Cell(2, 8).GetValue<decimal>());
        Assert.Equal(45_000m, sheet.Cell(3, 8).GetValue<decimal>());
        Assert.Equal(50_000m, sheet.Cell(4, 8).GetValue<decimal>());
        Assert.Equal(BaseCop, sheet.Cell(5, 8).GetValue<decimal>());
    }

    // Spec 2026-09-17, punto 8a: el nombre del archivo lleva la hora del tenant. Con el reloj en el 31
    // de diciembre a las 23:00 de Bogotá no dice 2027.
    [Fact]
    public async Task ExportNamesTheFileWithTheTenantsLocalTime()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), storage, new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero));
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        await SeedTenantAsync(factory);
        await CreateProductAsync(client, "AAA-1", "Vela de soja", []);

        var response = await client.PostAsync(
            ExportUrl(), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("productos-2026-12-31-2300.xlsx", body.FileName);
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

    /// <summary>
    /// El `search` del export es el mismo de la caja de búsqueda del listado: nombre del producto,
    /// código o código de barras de alguna variante, con los comodines de LIKE escapados. Se
    /// exporta lo que se está viendo en la grilla, y la grilla filtra con esa caja.
    /// </summary>
    [Fact]
    public async Task ExportFiltersBySearchLikeTheList()
    {
        await using var database = await StartDatabaseAsync();
        var storage = new CapturingExportStorage();
        using var factory = new QepApiFactory(database.GetConnectionString(), storage);
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        await SeedTenantAsync(factory);

        await CreateProductAsync(client, "VS-001", "Vela de soja", []);
        await CreateProductAsync(client, "DIF-002", "Difusor de madera", [], barcode: "7700000000123");
        await CreateProductAsync(client, "JAB-003", "Jabon artesanal", []);

        async Task<string[]> ExportedCodesAsync(string query)
        {
            var response = await client.PostAsync(
                $"{ExportUrl()}?{query}", content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.NotNull(storage.Content);
            using var workbook = new XLWorkbook(new MemoryStream(storage.Content));
            var sheet = workbook.Worksheets.First();
            return Enumerable.Range(2, sheet.LastRowUsed()!.RowNumber() - 1)
                .Select(row => sheet.Cell(row, 1).GetString())
                .ToArray();
        }

        // Por nombre, sin distinguir mayúsculas.
        Assert.Equal(["VS-001"], await ExportedCodesAsync("search=vela"));
        // Por código de variante.
        Assert.Equal(["JAB-003"], await ExportedCodesAsync("search=jab-0"));
        // Por código de barras de variante.
        Assert.Equal(["DIF-002"], await ExportedCodesAsync("search=0000000123"));
        // Combinado con `code` por AND, igual que el listado.
        Assert.Equal(["DIF-002"], await ExportedCodesAsync("search=d&code=DIF"));

        // `_` es literal, no un comodín: ningún producto lo contiene, así que no hay qué exportar.
        var wildcard = await client.PostAsync(
            $"{ExportUrl()}?search=_", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wildcard.StatusCode);
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

    /// <summary>Precio base comun a los tres productos, para que los finales derivados de cada
    /// escala (base x (1 - desc/100)) sean predecibles en la hoja.</summary>
    private const decimal BaseCop = 50_000m;

    private static object Scale(int fromUnit, int toUnit, decimal discount) => new
    {
        fromUnit,
        toUnit,
        discount,
        restriction = "multiple",
        multiple = 1,
    };

    // Este archivo usa un tenant que no pasa por el registro. ExportProductsHandler nombra el archivo
    // con la hora del tenant (spec 2026-09-17, punto 8a), y sin su fila en tenancy.tenants
    // TenantClock responde tenancy.tenant.not_found. Spec 2026-10-07: con fila el stub enmascara, así
    // que el tenant nace con sus siete módulos, en el mismo SaveChangesAsync.
    private static async Task SeedTenantAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(TenantId));
        var now = DateTimeOffset.UtcNow;
        tenancy.Tenants.Add(Modules.Tenancy.Domain.Tenant.Create(
            id,
            "catalog-export-tests",
            "Catalog Export Tests",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            Modules.Tenancy.Domain.MembershipId.New(),
            now));
        foreach (var key in Modules.Tenancy.Domain.TenantModuleKeys.All)
        {
            tenancy.TenantModules.Add(Modules.Tenancy.Domain.TenantModule.Create(
                id, key, Modules.Tenancy.Domain.TenantModuleSources.Seed, now, note: null));
        }

        await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task CreateProductAsync(
        HttpClient client, string code, string name, object[] scales, string? barcode = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            ProductBodies.Create(
                name,
                code,
                new { prices = new Dictionary<string, decimal> { ["COP"] = BaseCop }, scales },
                barcode: barcode),
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

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class QepApiFactory(
        string connectionString, IProductExportStorage exportStorage, DateTimeOffset? utcNow = null)
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
            builder.ConfigureServices(services =>
            {
                services.AddScoped(_ => exportStorage);
                // Reloj fijo sólo para las pruebas que lo piden (spec 2026-09-17).
                if (utcNow is { } fixedNow)
                {
                    services.RemoveAll<IClock>();
                    services.AddScoped<IClock>(_ => new FixedClock(fixedNow));
                }
            });
        }
    }
}

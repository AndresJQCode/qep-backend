using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// <c>POST /catalog/products/price-scales/copy</c> — copiar el juego de escalas de un producto a
/// varios otros de una sola vez.
///
/// La forma de la copia ya la cubren las unitarias de <c>PriceScaleCopyTests</c> contra el
/// agregado en memoria. Lo que este archivo verifica es lo que ésas no pueden ver: que el lote
/// commitee de una sola vez contra Postgres, que las escalas viejas del destino desaparezcan de
/// verdad en vez de quedar huérfanas, y que un destino inválido no deje media copia escrita.
/// </summary>
public sealed class CopyPriceScalesApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000061";
    private const string SubjectId = "01900000-0000-7000-8000-000000000062";

    private static readonly string[] ManagePermissions =
    [
        CatalogPermissions.ProductRead, CatalogPermissions.ProductManage
    ];

    private static readonly string[] ReadOnlyPermissions = [CatalogPermissions.ProductRead];

    [Fact]
    public async Task CopiesTheScalesToEveryTargetInOneCall()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales());
        // Precios base distintos del origen a propósito: es el caso que el lote del navegador no
        // podía resolver, porque arrastraba el precio final del origen.
        var first = await CreateProductAsync(client, "Vela de cera", "VC-001", 50m);
        var second = await CreateProductAsync(client, "Vela de palma", "VP-001", 200m);

        var response = await CopyAsync(client, source.Id, [first.Id, second.Id]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var copied = await ReadCopyAsync(response);
        Assert.Equal(2, copied.Products.Count);

        // Releídos desde la base, no desde la respuesta de la escritura: eso probaría el mapeo de
        // salida, no que el lote haya commiteado.
        var reloadedFirst = await GetProductAsync(client, first.Id);
        var reloadedSecond = await GetProductAsync(client, second.Id);

        Assert.Equal(2, reloadedFirst.PriceScales.Count);
        Assert.Equal(2, reloadedSecond.PriceScales.Count);

        // 10% sobre el precio base de cada destino, no el final del origen (90).
        var firstDiscounted = Assert.Single(
            reloadedFirst.PriceScales, scale => scale.FromUnit == 1);
        Assert.Equal(45m, firstDiscounted.FinalUsd);

        var secondDiscounted = Assert.Single(
            reloadedSecond.PriceScales, scale => scale.FromUnit == 1);
        Assert.Equal(180m, secondDiscounted.FinalUsd);

        // El precio base del destino no se toca: se copian las escalas, no el precio.
        Assert.Equal(50m, reloadedFirst.PriceBaseUsd);
        Assert.Equal(200m, reloadedSecond.PriceBaseUsd);
    }

    // El reemplazo tiene que borrar las filas viejas de verdad. Contra el agregado en memoria esto
    // se ve igual con o sin cascada configurada; contra Postgres, no.
    [Fact]
    public async Task ReplacesTheScalesTheTargetAlreadyHad()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales());
        var target = await CreateProductAsync(
            client, "Vela de cera", "VC-001", 100m, OneScale(fromUnit: 100, toUnit: 200));

        Assert.Single(target.PriceScales);

        await CopyAsync(client, source.Id, [target.Id]);

        var reloaded = await GetProductAsync(client, target.Id);
        Assert.Equal(2, reloaded.PriceScales.Count);
        Assert.DoesNotContain(reloaded.PriceScales, scale => scale.FromUnit == 100);
    }

    // La diferencia con el lote que armaba el cliente: un destino que no existe tumba la operación
    // entera en vez de dejar la mitad escrita.
    [Fact]
    public async Task WritesNothingWhenOneTargetDoesNotExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales());
        var target = await CreateProductAsync(client, "Vela de cera", "VC-001", 100m);

        var response = await CopyAsync(client, source.Id, [target.Id, Guid.NewGuid()]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var reloaded = await GetProductAsync(client, target.Id);
        Assert.Empty(reloaded.PriceScales);
    }

    // Un origen sin escalas sería un borrado masivo disfrazado de copia.
    [Fact]
    public async Task RejectsASourceWithoutScales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m);
        var target = await CreateProductAsync(
            client, "Vela de cera", "VC-001", 100m, OneScale());

        var response = await CopyAsync(client, source.Id, [target.Id]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("catalog.product.price_scale_copy.source_without_scales", problem.Code);

        // Y el destino conserva las suyas.
        var reloaded = await GetProductAsync(client, target.Id);
        Assert.Single(reloaded.PriceScales);
    }

    [Fact]
    public async Task RejectsAnInactiveTarget()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales());
        var target = await CreateProductAsync(client, "Vela de cera", "VC-001", 100m);

        var deactivated = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{target.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var response = await CopyAsync(client, source.Id, [target.Id]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("catalog.product.inactive", problem.Code);
    }

    [Fact]
    public async Task RejectsTheSourceAsItsOwnTarget()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales());

        var response = await CopyAsync(client, source.Id, [source.Id]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("validation.failed", problem.Code);
    }

    [Fact]
    public async Task RejectsAnEmptyTargetList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales());

        var response = await CopyAsync(client, source.Id, []);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("validation.failed", problem.Code);
    }

    // Copiar escalas es escribir en el catálogo: la lectura no alcanza.
    [Fact]
    public async Task RejectsACallerWithoutTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(manager, "Vela de soja", "VS-001", 100m, TwoScales());
        var target = await CreateProductAsync(manager, "Vela de cera", "VC-001", 100m);

        using var reader = CreateClient(factory, SubjectId, TenantId, ReadOnlyPermissions);
        var response = await CopyAsync(reader, source.Id, [target.Id]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Helpers ----

    private static object[] TwoScales() =>
    [
        new
        {
            fromUnit = 1,
            toUnit = 9,
            discount = 10m,
            restriction = "multiple",
            multiple = 3,
            finalUsd = 90m
        },
        new
        {
            fromUnit = 10,
            toUnit = 50,
            discount = 20m,
            restriction = "packaging_unit",
            packagingUnit = 12,
            finalUsd = 80m
        }
    ];

    private static object[] OneScale(int fromUnit = 1, int toUnit = 9) =>
    [
        new
        {
            fromUnit,
            toUnit,
            discount = 10m,
            restriction = "multiple",
            multiple = 3,
            finalUsd = 90m
        }
    ];

    private static async Task<ProductResponse> CreateProductAsync(
        HttpClient client,
        string name,
        string code,
        decimal baseUsd,
        object[]? scales = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name,
                code,
                pricing = new { baseUsd, scales = scales ?? [] }
            },
            TestContext.Current.CancellationToken);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    private static Task<HttpResponseMessage> CopyAsync(
        HttpClient client,
        Guid sourceProductId,
        Guid[] targetProductIds) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/price-scales/copy",
            new { sourceProductId, targetProductIds },
            TestContext.Current.CancellationToken);

    private static async Task<CopyPriceScalesResponse> ReadCopyAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<CopyPriceScalesResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        return payload;
    }

    private static async Task<ProductResponse> GetProductAsync(HttpClient client, Guid productId)
    {
        var product = await client.GetFromJsonAsync<ProductResponse>(
            $"/api/v1/tenants/{TenantId}/catalog/products/{productId}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    private static async Task<ProblemPayload> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
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

    /// <summary>Las extensiones de ProblemDetails llegan aplanadas en la raiz
    /// (ApiExceptionHandler).</summary>
    private sealed record ProblemPayload(string Code);
}

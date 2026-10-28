using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// <c>POST /catalog/products/price-scales/copy</c> — copiar el juego de escalas de una variante a
/// varias otras, de uno o de varios productos, de una sola vez (spec 2026-10-20 §4). Los productos
/// sin opciones de la mayoría de estas pruebas tienen una sola variante, que es el caso «como antes».
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
    private const string OtherTenantId = "01900000-0000-7000-8000-000000000063";
    private const string OtherSubjectId = "01900000-0000-7000-8000-000000000064";

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

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);
        // Precios base distintos del origen a propósito: es el caso que el lote del navegador no
        // podía resolver, porque arrastraba el precio final del origen.
        var first = await CreateProductAsync(client, "Vela de cera", "VC-001", 50m);
        var second = await CreateProductAsync(client, "Vela de palma", "VP-001", 200m);

        var response = await CopyAsync(client, source.Only().Id, [first.Only().Id, second.Only().Id]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var copied = await ReadCopyAsync(response);
        Assert.Equal(2, copied.Products.Count);

        // Releídos desde la base, no desde la respuesta de la escritura: eso probaría el mapeo de
        // salida, no que el lote haya commiteado.
        var reloadedFirst = await GetProductAsync(client, first.Id);
        var reloadedSecond = await GetProductAsync(client, second.Id);

        Assert.Equal(2, reloadedFirst.Only().PriceScales.Count);
        Assert.Equal(2, reloadedSecond.Only().PriceScales.Count);

        // 10% sobre el precio base de cada destino, no el final del origen (90).
        var firstDiscounted = Assert.Single(
            reloadedFirst.Only().PriceScales, scale => scale.FromUnit == 1);
        Assert.Equal(45m, firstDiscounted.Finals["USD"]);

        var secondDiscounted = Assert.Single(
            reloadedSecond.Only().PriceScales, scale => scale.FromUnit == 1);
        Assert.Equal(180m, secondDiscounted.Finals["USD"]);

        // El precio base del destino no se toca: se copian las escalas, no el precio.
        Assert.Equal(50m, reloadedFirst.Only().Prices["USD"]);
        Assert.Equal(200m, reloadedSecond.Only().Prices["USD"]);
    }

    // La restricción no viaja: la escala llega incompleta y así se lee de vuelta desde Postgres,
    // con la columna ya nullable. Es lo que el frontend usa para marcarla por completar.
    [Fact]
    public async Task CopiesTheScalesWithoutTheirRestriction()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);
        var target = await CreateProductAsync(client, "Vela de cera", "VC-001", 100m);

        var response = await CopyAsync(client, source.Only().Id, [target.Only().Id]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reloaded = await GetProductAsync(client, target.Id);
        Assert.Equal(2, reloaded.Only().PriceScales.Count);
        Assert.All(reloaded.Only().PriceScales, scale =>
        {
            Assert.Null(scale.Restriction);
            Assert.Null(scale.Multiple);
            Assert.False(scale.AllowGrouping);
        });
        Assert.Contains(reloaded.Only().PriceScales, scale => scale is { FromUnit: 1, Discount: 10m });
        Assert.Contains(reloaded.Only().PriceScales, scale => scale is { FromUnit: 10, Discount: 20m });
    }

    // El reemplazo tiene que borrar las filas viejas de verdad. Contra el agregado en memoria esto
    // se ve igual con o sin cascada configurada; contra Postgres, no.
    [Fact]
    public async Task ReplacesTheScalesTheTargetAlreadyHad()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);
        var target = await CreateProductAsync(
            client, "Vela de cera", "VC-001", 100m, OneScale(fromUnit: 100, toUnit: 200));

        Assert.Single(target.Only().PriceScales);

        await CopyAsync(client, source.Only().Id, [target.Only().Id]);

        var reloaded = await GetProductAsync(client, target.Id);
        Assert.Equal(2, reloaded.Only().PriceScales.Count);
        Assert.DoesNotContain(reloaded.Only().PriceScales, scale => scale.FromUnit == 100);
    }

    // La diferencia con el lote que armaba el cliente: un destino que no existe tumba la operación
    // entera en vez de dejar la mitad escrita.
    [Fact]
    public async Task WritesNothingWhenOneTargetDoesNotExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);
        var target = await CreateProductAsync(client, "Vela de cera", "VC-001", 100m);

        var response = await CopyAsync(client, source.Only().Id, [target.Only().Id, Guid.NewGuid()]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var reloaded = await GetProductAsync(client, target.Id);
        Assert.Empty(reloaded.Only().PriceScales);
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

        var response = await CopyAsync(client, source.Only().Id, [target.Only().Id]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("catalog.product.price_scale_copy.source_without_scales", problem.Code);

        // Y el destino conserva las suyas.
        var reloaded = await GetProductAsync(client, target.Id);
        Assert.Single(reloaded.Only().PriceScales);
    }

    [Fact]
    public async Task RejectsAnInactiveTarget()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);
        var target = await CreateProductAsync(client, "Vela de cera", "VC-001", 100m);

        var deactivated = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{target.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var response = await CopyAsync(client, source.Only().Id, [target.Only().Id]);

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

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);

        var response = await CopyAsync(client, source.Only().Id, [source.Only().Id]);

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

        var source = await CreateProductAsync(client, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);

        var response = await CopyAsync(client, source.Only().Id, []);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("validation.failed", problem.Code);
    }

    // Tarea 7. Origen: la talla S de una camiseta (con sus escalas). Destinos: la talla M de la
    // misma camiseta y la única variante de una vela, o sea variantes de DOS productos en una sola
    // llamada. Cada destino queda con las escalas del origen y su precio propio; el origen y la
    // otra talla (L, que no es destino) no se tocan.
    [Fact]
    public async Task CopyFromVariantToVariantsOfSeveralProducts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var talla = await CreateOptionAsync(client, TenantId, "Talla", "S", "M", "L");
        var shirt = await CreateShirtAsync(client, talla, small: (20m, TwoScales()), medium: (30m, null), large: (40m, OneScale(fromUnit: 100, toUnit: 200)));
        var candle = await CreateProductAsync(client, "Vela de cera", "VC-001", 50m);
        var (small, medium, large) = (shirt.Variants[0], shirt.Variants[1], shirt.Variants[2]);

        var response = await CopyAsync(client, small.Id, [candle.Only().Id, medium.Id]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var copied = await ReadCopyAsync(response);
        // Un producto una sola vez, en el orden de su primera variante de destino: la vela primero.
        Assert.Equal([candle.Id, shirt.Id], copied.Products.Select(product => product.Id));
        // La camiseta vuelve entera (sus tres tallas) con canDelete resuelto por la consulta por lote.
        var shirtInResponse = Assert.Single(copied.Products, product => product.Id == shirt.Id);
        Assert.Equal(3, shirtInResponse.Variants.Count);
        Assert.All(shirtInResponse.Variants, variant => Assert.True(variant.CanDelete));

        var reloadedShirt = await GetProductAsync(client, shirt.Id);
        var reloadedCandle = await GetProductAsync(client, candle.Id);
        var reloadedSmall = reloadedShirt.Variants.Single(variant => variant.Id == small.Id);
        var reloadedMedium = reloadedShirt.Variants.Single(variant => variant.Id == medium.Id);
        var reloadedLarge = reloadedShirt.Variants.Single(variant => variant.Id == large.Id);

        Assert.Equal(2, reloadedMedium.PriceScales.Count);
        Assert.Equal(2, reloadedCandle.Only().PriceScales.Count);
        // 10% sobre el precio base de cada destino, no sobre el del origen (20).
        Assert.Equal(27m, Assert.Single(reloadedMedium.PriceScales, scale => scale.FromUnit == 1).Finals["USD"]);
        Assert.Equal(45m, Assert.Single(reloadedCandle.Only().PriceScales, scale => scale.FromUnit == 1).Finals["USD"]);
        Assert.All(reloadedMedium.PriceScales.Concat(reloadedCandle.Only().PriceScales), scale => Assert.Null(scale.Restriction));
        Assert.Equal(30m, reloadedMedium.Prices["USD"]);
        Assert.Equal(50m, reloadedCandle.Only().Prices["USD"]);

        // El origen conserva sus escalas completas y la talla que no era destino, las suyas.
        Assert.Equal(2, reloadedSmall.PriceScales.Count);
        Assert.All(reloadedSmall.PriceScales, scale => Assert.NotNull(scale.Restriction));
        var largeScale = Assert.Single(reloadedLarge.PriceScales);
        Assert.Equal(100, largeScale.FromUnit);
    }

    // Tope de 200 contado en variantes: 201 destinos dan 422 con el mapa errors en el campo del
    // cuerpo. La validación corre antes de tocar la base, así que los ids pueden ser cualquiera.
    [Fact]
    public async Task CopyRejects201Targets()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var targets = Enumerable.Range(0, PriceScaleCopyLimits.MaxTargets + 1).Select(_ => Guid.NewGuid()).ToArray();

        var response = await CopyAsync(client, Guid.NewGuid(), targets);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("TargetVariantIds", out var messages));
        Assert.True(messages.GetArrayLength() >= 1);
    }

    // El borde de arriba: 200 destinos pasan el validador y recién ahí no se encuentran (404), no 422.
    [Fact]
    public async Task CopyAccepts200TargetsAtTheValidator()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var targets = Enumerable.Range(0, PriceScaleCopyLimits.MaxTargets).Select(_ => Guid.NewGuid()).ToArray();

        var response = await CopyAsync(client, Guid.NewGuid(), targets);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("catalog.variant.not_found", (await ReadProblemAsync(response)).Code);
    }

    // Un origen que no existe es 404 con el código de variante, y no deja nada escrito.
    [Fact]
    public async Task CopyFromAMissingSourceVariantIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var target = await CreateProductAsync(client, "Vela de cera", "VC-001", 100m, OneScale());

        var response = await CopyAsync(client, Guid.NewGuid(), [target.Only().Id]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("catalog.variant.not_found", (await ReadProblemAsync(response)).Code);
        Assert.Single((await GetProductAsync(client, target.Id)).Only().PriceScales);
    }

    // Aislamiento de tenant, primera capa: el tenant de la ruta no es el del llamador, 403.
    [Fact]
    public async Task CopyRejectsACallerFromAnotherTenantWith403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var outsider = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        var source = await CreateProductAsync(manager, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);
        var target = await CreateProductAsync(manager, "Vela de cera", "VC-001", 100m);

        var response = await CopyAsync(outsider, source.Only().Id, [target.Only().Id]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await GetProductAsync(manager, target.Id)).Only().PriceScales);
    }

    // Segunda capa: con la ruta de SU tenant, una variante de otro tenant se ve igual que una que
    // no existe (404, sin confirmar que existe) y la del otro tenant no se toca.
    [Fact]
    public async Task CopyNeverReachesAVariantOfAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var outsider = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        var source = await CreateProductAsync(manager, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);
        var foreignTarget = await CreateProductAsync(outsider, "Vela ajena", "VA-001", 100m, tenantId: OtherTenantId);

        var response = await CopyAsync(manager, source.Only().Id, [foreignTarget.Only().Id]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var untouched = await outsider.GetFromJsonAsync<ProductResponse>(
            $"/api/v1/tenants/{OtherTenantId}/catalog/products/{foreignTarget.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(untouched);
        Assert.Empty(untouched.Only().PriceScales);
    }

    // Copiar escalas es escribir en el catálogo: la lectura no alcanza.
    [Fact]
    public async Task RejectsACallerWithoutTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var source = await CreateProductAsync(manager, "Vela de soja", "VS-001", 100m, TwoScales(), TwelvePack);
        var target = await CreateProductAsync(manager, "Vela de cera", "VC-001", 100m);

        using var reader = CreateClient(factory, SubjectId, TenantId, ReadOnlyPermissions);
        var response = await CopyAsync(reader, source.Only().Id, [target.Only().Id]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Helpers ----

    // El empaque de la escala packaging_unit de TwoScales: es del producto desde el 2026-10-01,
    // así que viaja en el precio y no dentro de la escala.
    private static readonly int[] TwelvePack = [12];

    private static object[] TwoScales() =>
    [
        new
        {
            fromUnit = 1,
            toUnit = 9,
            discount = 10m,
            restriction = "multiple",
            multiple = 3
        },
        new
        {
            fromUnit = 10,
            toUnit = 50,
            discount = 20m,
            restriction = "packaging_unit"
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
            multiple = 3
        }
    ];

    private static async Task<ProductResponse> CreateProductAsync(
        HttpClient client,
        string name,
        string code,
        decimal baseUsd,
        object[]? scales = null,
        int[]? packagingUnits = null,
        string tenantId = TenantId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            ProductBodies.Create(
                name,
                code,
                pricing: new { prices = new Dictionary<string, decimal> { ["USD"] = baseUsd }, scales = scales ?? [], packagingUnits = packagingUnits ?? [] }),
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

    // «Camiseta» con Talla S / M / L: tres variantes de un mismo producto, cada una con su precio y
    // sus escalas. Vuelven en el orden en que se mandaron.
    private static async Task<ProductResponse> CreateShirtAsync(
        HttpClient client,
        OptionResponse talla,
        (decimal Usd, object[]? Scales) small,
        (decimal Usd, object[]? Scales) medium,
        (decimal Usd, object[]? Scales) large)
    {
        object VariantOf(string code, string size, (decimal Usd, object[]? Scales) spec) => new
        {
            code,
            optionValueIds = new[] { talla.Values.Single(value => value.Name == size).Id },
            pricing = new
            {
                prices = new Dictionary<string, decimal> { ["USD"] = spec.Usd },
                scales = spec.Scales ?? [],
                packagingUnits = TwelvePack
            }
        };

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new
            {
                name = "Camiseta",
                optionIds = new[] { talla.Id },
                variants = new[] { VariantOf("CAM-S", "S", small), VariantOf("CAM-M", "M", medium), VariantOf("CAM-L", "L", large) }
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

    private static Task<HttpResponseMessage> CopyAsync(
        HttpClient client,
        Guid sourceVariantId,
        Guid[] targetVariantIds,
        string tenantId = TenantId) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/price-scales/copy",
            new { sourceVariantId, targetVariantIds },
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

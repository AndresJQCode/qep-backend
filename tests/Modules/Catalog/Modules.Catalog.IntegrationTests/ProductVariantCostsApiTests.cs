using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using static Modules.Catalog.IntegrationTests.CatalogTestDatabase;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// El costo unitario de una variante en el API de catálogo: se lee con
/// <c>catalog.product_cost.read</c> (o manage), se escribe con <c>.manage</c>, y un cliente que
/// no lo conoce ni lo ve ni lo borra al editar el resto del producto.
/// </summary>
public sealed class ProductVariantCostsApiTests(SharedCatalogHost sharedHost) : IClassFixture<SharedCatalogHost>
{
    private string TenantId { get; } = Guid.CreateVersion7().ToString();
    private const string SubjectId = "01900000-0000-7000-8000-000000000072";

    private static readonly string[] Base = [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    private static readonly string[] CostManager =
        [.. Base, CatalogPermissions.ProductCostRead, CatalogPermissions.ProductCostManage];

    private static readonly Dictionary<string, decimal> TwelveThousand = new() { ["COP"] = 12_000m };

    [Fact]
    public async Task GetWithoutCostReadReturnsNullCosts()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        using var reader = Client(factory, [CatalogPermissions.ProductRead]);
        var created = await CreateAsync(manager, "VC-1", TwelveThousand);

        using var json = await GetJsonAsync(reader, created.Id);

        var costs = json.RootElement.GetProperty("variants")[0].GetProperty("costs");
        Assert.Equal(JsonValueKind.Null, costs.ValueKind);
    }

    [Fact]
    public async Task GetWithCostReadReturnsTheMapOrEmpty()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        using var reader = Client(factory, [CatalogPermissions.ProductRead, CatalogPermissions.ProductCostRead]);
        var withCost = await CreateAsync(manager, "VC-1", TwelveThousand);
        var without = await CreateAsync(manager, "VC-2", null);

        using var withJson = await GetJsonAsync(reader, withCost.Id);
        using var withoutJson = await GetJsonAsync(reader, without.Id);

        var costs = withJson.RootElement.GetProperty("variants")[0].GetProperty("costs");
        Assert.Equal(12_000m, costs.GetProperty("COP").GetDecimal());
        var empty = withoutJson.RootElement.GetProperty("variants")[0].GetProperty("costs");
        Assert.Equal(JsonValueKind.Object, empty.ValueKind);
        Assert.Empty(empty.EnumerateObject());
    }

    [Fact]
    public async Task ManageWithoutReadAlsoSeesCosts()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, [.. Base, CatalogPermissions.ProductCostManage]);
        var created = await CreateAsync(manager, "VC-1", TwelveThousand);

        using var json = await GetJsonAsync(manager, created.Id);

        Assert.Equal(12_000m, json.RootElement.GetProperty("variants")[0].GetProperty("costs").GetProperty("COP").GetDecimal());
    }

    [Fact]
    public async Task PostWithCostsWithoutManageIs403()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = Client(factory, Base);

        var response = await client.PostAsJsonAsync(
            ProductsUrl,
            ProductBodies.Create("Vela", "VC-1", costs: TwelveThousand),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("catalog.product_cost.forbidden", body.RootElement.GetProperty("code").GetString());

        var list = await client.GetStringAsync(ProductsUrl, TestContext.Current.CancellationToken);
        using var listJson = JsonDocument.Parse(list);
        Assert.Empty(listJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PutWithCostsWithoutManageIs403AndChangesNothing()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        using var client = Client(factory, Base);
        var created = await CreateAsync(manager, "VC-1", TwelveThousand);

        var response = await client.PutAsJsonAsync(
            $"{ProductsUrl}/{created.Id}",
            ProductBodies.Update(created, "Otro nombre", "VC-1", costs: new Dictionary<string, decimal> { ["COP"] = 1m }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("catalog.product_cost.forbidden", body.RootElement.GetProperty("code").GetString());

        using var after = await GetJsonAsync(manager, created.Id);
        Assert.Equal(created.Version, after.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(12_000m, after.RootElement.GetProperty("variants")[0].GetProperty("costs").GetProperty("COP").GetDecimal());
    }

    [Fact]
    public async Task PutWithoutCostsKeepsTheSavedCostForAManagerWithoutCostPermissions()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        using var editor = Client(factory, Base);
        var created = await CreateAsync(manager, "VC-1", TwelveThousand);

        var response = await editor.PutAsJsonAsync(
            $"{ProductsUrl}/{created.Id}",
            ProductBodies.Update(created, "Otro nombre", "VC-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var put = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, put.RootElement.GetProperty("variants")[0].GetProperty("costs").ValueKind);

        using var after = await GetJsonAsync(manager, created.Id);
        Assert.Equal(12_000m, after.RootElement.GetProperty("variants")[0].GetProperty("costs").GetProperty("COP").GetDecimal());
    }

    [Fact]
    public async Task PutWithAnEmptyMapClearsTheCost()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        var created = await CreateAsync(manager, "VC-1", TwelveThousand);

        var response = await manager.PutAsJsonAsync(
            $"{ProductsUrl}/{created.Id}",
            ProductBodies.Update(created, "Vela", "VC-1", costs: new Dictionary<string, decimal>()),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var after = await GetJsonAsync(manager, created.Id);
        Assert.Empty(after.RootElement.GetProperty("variants")[0].GetProperty("costs").EnumerateObject());
    }

    [Fact]
    public async Task ZeroOrUnknownCurrencyCostIs422WithTheField()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);

        var zero = await manager.PostAsJsonAsync(
            ProductsUrl,
            ProductBodies.Create("Vela", "VC-1", costs: new Dictionary<string, decimal> { ["COP"] = 0m }),
            TestContext.Current.CancellationToken);
        var unknown = await manager.PostAsJsonAsync(
            ProductsUrl,
            ProductBodies.Create("Vela", "VC-2", costs: new Dictionary<string, decimal> { ["XXX"] = 5m }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);
        using var zeroBody = JsonDocument.Parse(await zero.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", zeroBody.RootElement.GetProperty("code").GetString());
        Assert.True(zeroBody.RootElement.GetProperty("errors").TryGetProperty("Variants[0].Costs.COP", out _));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        using var unknownBody = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var field = unknownBody.RootElement.GetProperty("errors").GetProperty("Variants[0].Costs.XXX");
        // `errors` carries messages only (ApiExceptionHandler); the row's error code stays server-side,
        // exactly as for a price row.
        Assert.Contains("not supported", field.GetRawText());
    }

    [Fact]
    public async Task ASubCentCostIs422WithTheFieldNotA500()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);

        var response = await manager.PostAsJsonAsync(
            ProductsUrl,
            ProductBodies.Create("Vela", "VC-3", costs: new Dictionary<string, decimal> { ["COP"] = 0.001m }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Variants[0].Costs.COP", out _));
    }

    [Fact]
    public async Task TheProductListAndSelectorNeverCarryCosts()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var manager = Client(factory, CostManager);
        await CreateAsync(manager, "VC-1", TwelveThousand);

        var raw = await manager.GetStringAsync(
            $"{ProductsUrl}?includeVariants=true", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("\"costs\"", raw, StringComparison.OrdinalIgnoreCase);
    }

    private string ProductsUrl => $"/api/v1/tenants/{TenantId}/catalog/products";

    private async Task<ProductResponse> CreateAsync(
        HttpClient client, string code, Dictionary<string, decimal>? costs)
    {
        var response = await client.PostAsJsonAsync(
            ProductsUrl, ProductBodies.Create("Vela", code, costs: costs), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(TestContext.Current.CancellationToken);
        return product!;
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient client, Guid productId) =>
        JsonDocument.Parse(await client.GetStringAsync($"{ProductsUrl}/{productId}", TestContext.Current.CancellationToken));

    private HttpClient Client(QepApiFactory factory, string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
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
}

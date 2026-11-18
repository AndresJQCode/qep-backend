using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Spec 2026-11-18 §3: el tipo (producto o servicio) y la unidad de venta del producto, por nombre en el
/// alta, la edición, el detalle y el listado, con el filtro <c>?kind=</c>.
/// </summary>
public sealed class ProductKindApiTests(SharedCatalogHost sharedHost) : IClassFixture<SharedCatalogHost>
{
    private string TenantId { get; } = Guid.CreateVersion7().ToString();
    private const string SubjectId = "01900000-0000-7000-8000-0000000000a3";

    private static readonly string[] ManagePermissions =
        [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    private static readonly int[] PackagingOfTwelve = [12];

    [Fact]
    public async Task CreateWithoutKindIsPhysicalByUnit()
    {
        using var client = await ClientAsync();

        var response = await PostAsync(client, ProductBodies.Create("Vela", "V-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.Equal("Physical", created.Kind);
        Assert.Equal("Unit", created.SalesUnit);
    }

    [Fact]
    public async Task CreateAServiceByHourRoundTrips()
    {
        using var client = await ClientAsync();

        var response = await PostAsync(client, ServiceBody("Instalación", "SRV-1", "Service", "Hour"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        var json = await client.GetStringAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Contains("\"kind\":\"Service\"", json, StringComparison.Ordinal);
        Assert.Contains("\"salesUnit\":\"Hour\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateChangesKindAndUnit()
    {
        using var client = await ClientAsync();
        var created = await ReadProductAsync(await PostAsync(client, ProductBodies.Create("Asesoría", "A-1")));

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new
            {
                expectedVersion = created.Version,
                name = "Asesoría",
                kind = "Service",
                salesUnit = "Month",
                variants = new[] { ProductBodies.Variant(created.Only().Id, "A-1", ProductBodies.UsdPricing()) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadProductAsync(response);
        Assert.Equal("Service", updated.Kind);
        Assert.Equal("Month", updated.SalesUnit);

        // Un PUT que no los manda los conserva: un cliente viejo no vuelve físico un servicio.
        var again = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            ProductBodies.Update(updated, "Asesoría", "A-1"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var kept = await ReadProductAsync(again);
        Assert.Equal("Service", kept.Kind);
        Assert.Equal("Month", kept.SalesUnit);
    }

    [Fact]
    public async Task ListFiltersByKind()
    {
        using var client = await ClientAsync();
        var product = await ReadProductAsync(await PostAsync(client, ProductBodies.Create("Vela", "V-1")));
        var service = await ReadProductAsync(
            await PostAsync(client, ServiceBody("Instalación", "SRV-1", "Service", "Hour")));

        var services = await ListAsync(client, "kind=Service");
        var all = await ListAsync(client, string.Empty);

        var item = Assert.Single(services.Items);
        Assert.Equal(service.Id, item.Id);
        Assert.Equal("Service", item.Kind);
        Assert.Equal("Hour", item.SalesUnit);
        Assert.Equal(2, all.Total);
        Assert.Contains(all.Items, row => row.Id == product.Id && row.Kind == "Physical" && row.SalesUnit == "Unit");

        var invalid = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products?kind=Gadget", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task ServiceWithPackagingIs422()
    {
        using var client = await ClientAsync();
        var body = new
        {
            name = "Instalación",
            kind = "Service",
            salesUnit = "Hour",
            variants = new[]
            {
                ProductBodies.Variant(
                    null,
                    "SRV-1",
                    new { prices = new Dictionary<string, decimal> { ["USD"] = 10m }, packagingUnits = PackagingOfTwelve })
            }
        };

        var response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("catalog.product.packaging_not_allowed_for_service", content, StringComparison.Ordinal);
        Assert.Contains("\"PackagingUnits\"", content, StringComparison.Ordinal);
    }

    // La regla también corre en la edición: volver servicio un producto que conserva empaques responde el mismo 422.
    [Fact]
    public async Task TurningAProductWithPackagingIntoAServiceIs422()
    {
        using var client = await ClientAsync();
        var packaging = new { prices = new Dictionary<string, decimal> { ["USD"] = 10m }, packagingUnits = PackagingOfTwelve };
        var created = await ReadProductAsync(await PostAsync(client, new
        {
            name = "Caja de velas",
            variants = new[] { ProductBodies.Variant(null, "CV-1", packaging) }
        }));
        Assert.Equal("Physical", created.Kind);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new
            {
                expectedVersion = created.Version,
                name = "Caja de velas",
                kind = "Service",
                variants = new[] { ProductBodies.Variant(created.Only().Id, "CV-1", packaging) }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("catalog.product.packaging_not_allowed_for_service", content, StringComparison.Ordinal);
    }

    private static object ServiceBody(string name, string code, string kind, string salesUnit) =>
        new
        {
            name,
            kind,
            salesUnit,
            variants = new[] { ProductBodies.Variant(null, code, ProductBodies.UsdPricing()) }
        };

    private async Task<HttpClient> ClientAsync()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', ManagePermissions));
        return client;
    }

    private Task<HttpResponseMessage> PostAsync(HttpClient client, object body) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products", body, TestContext.Current.CancellationToken);

    private async Task<ProductsResponse> ListAsync(HttpClient client, string query)
    {
        var page = await client.GetFromJsonAsync<ProductsResponse>(
            $"/api/v1/tenants/{TenantId}/catalog/products?{query}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    private static async Task<ProductResponse> ReadProductAsync(HttpResponseMessage response)
    {
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Fijada, nunca heredada: los user-secrets del developer pueden prender la semilla.
            builder.UseSetting("Seed:Enabled", "false");
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

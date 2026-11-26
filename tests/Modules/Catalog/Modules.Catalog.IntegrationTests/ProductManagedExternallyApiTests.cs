using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// El PUT de la consola sobre un producto que administra Shopify (spec 2026-11-17 §5.5, plan D7):
/// lo que la tienda manda se rechaza con 422 <c>catalog.product.managed_externally</c> y el campo en
/// <c>errors</c>; lo demás se sigue editando. El origen se siembra por SQL: el único camino que lo
/// escribe es la importación, que vive en el módulo Shopify.
/// </summary>
public sealed class ProductManagedExternallyApiTests(SharedCatalogHost sharedHost) : IClassFixture<SharedCatalogHost>
{
    private string TenantId { get; } = Guid.CreateVersion7().ToString();
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";

    private static readonly string[] Permissions =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage
    ];

    [Fact]
    public async Task PutRenamingAnImportedProductIs422ManagedExternally()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory);
        var product = await CreateImportedProductAsync(client);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{product.Id}",
            ProductBodies.Update(product, "Vela de soja grande", "VS-001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("catalog.product.managed_externally", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task PutChangingTheTaxRateOfAnImportedProductIs200()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory);
        var product = await CreateImportedProductAsync(client);
        var taxRateId = await CreateTaxRateAsync(client);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{product.Id}",
            ProductBodies.Update(product, product.Name, "VS-001", taxRateId: taxRateId),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ProductResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(taxRateId, updated.TaxRateId);
        Assert.Equal("shopify", updated.ExternalSource);
    }

    // Crea por la API y marca el origen por SQL, filtrando por el id de esta prueba (la base es de
    // toda la clase). Devuelve el detalle releído, con la versión y el origen que quedaron.
    private async Task<ProductResponse> CreateImportedProductAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            ProductBodies.Create("Vela de soja", "VS-001"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = await created.Content.ReadFromJsonAsync<ProductResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(product);

        await using (var connection = new NpgsqlConnection(sharedHost.Database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "UPDATE catalog.products SET external_source = 'shopify' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", product.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var detail = await client.GetFromJsonAsync<ProductResponse>(
            $"/api/v1/tenants/{TenantId}/catalog/products/{product.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal("shopify", detail.ExternalSource);
        return detail;
    }

    private async Task<Guid> CreateTaxRateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates",
            new { name = "IVA general", percentage = 19m },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TaxRateResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    private HttpClient CreateClient(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', Permissions));
        return client;
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Fijado, nunca heredado: los user-secrets del developer pueden prender la semilla.
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

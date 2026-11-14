using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using static Modules.Catalog.IntegrationTests.CatalogTestDatabase;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// «Visible en la tienda»: el campo del producto (apagado por defecto), el filtro del listado y el
/// lote POST /catalog/products/store-visibility, que es todo o nada.
/// </summary>
public sealed class ProductStoreVisibilityApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-0000000000a1";
    private const string SubjectId = "01900000-0000-7000-8000-0000000000a2";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000b1";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000b2";

    private static readonly string[] ManagePermissions =
        [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    [Fact]
    public async Task PutWithoutVisibleInStoreKeepsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var created = await ReadProductAsync(await CreateProductAsync(client, TenantId, "Vela", "V-1"));
        Assert.False(created.VisibleInStore);
        var published = (await SetVisibilityAsync(client, TenantId, [created.Id], visible: true))
            .Items.Single();
        Assert.True(published.VisibleInStore);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            ProductBodies.Update(published.Version, created.Only().Id, "Vela de coco", "V-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadProductAsync(response)).VisibleInStore);
    }

    [Fact]
    public async Task TheBatchFlipsEveryProductAndBumpsTheirVersions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var first = await ReadProductAsync(await CreateProductAsync(client, TenantId, "Vela uno", "V-1"));
        var second = await ReadProductAsync(await CreateProductAsync(client, TenantId, "Vela dos", "V-2"));

        var result = await SetVisibilityAsync(client, TenantId, [first.Id, second.Id], visible: true);

        Assert.All(result.Items, item => Assert.True(item.VisibleInStore));
        Assert.Equal(
            [first.Version + 1, second.Version + 1],
            result.Items.Select(item => item.Version).ToArray());

        // Repetir el valor no sube la versión.
        var again = await SetVisibilityAsync(client, TenantId, [first.Id, second.Id], visible: true);
        Assert.Equal(result.Items.Select(item => item.Version), again.Items.Select(item => item.Version));
    }

    [Fact]
    public async Task TheBatchIsAllOrNothingWithAnotherTenantsProduct()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var mine = CreateClient(factory, SubjectId, TenantId);
        using var theirs = CreateClient(factory, OtherSubjectId, OtherTenantId);
        var own = await ReadProductAsync(await CreateProductAsync(mine, TenantId, "Vela", "V-1"));
        var foreign = await ReadProductAsync(await CreateProductAsync(theirs, OtherTenantId, "Ajena", "A-1"));

        var response = await mine.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/store-visibility",
            new { productIds = new[] { own.Id, foreign.Id }, visible = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(
            "catalog.product.not_found",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.False(await QueryVisibleAsync(connection, own.Id));
        Assert.False(await QueryVisibleAsync(connection, foreign.Id));
    }

    [Fact]
    public async Task TheBatchRejectsMoreThan200Ids()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/store-visibility",
            new { productIds = Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()).ToArray(), visible = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "validation.failed",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListFiltersByVisibleInStore()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var shown = await ReadProductAsync(await CreateProductAsync(client, TenantId, "Vela uno", "V-1"));
        var hidden = await ReadProductAsync(await CreateProductAsync(client, TenantId, "Vela dos", "V-2"));
        await SetVisibilityAsync(client, TenantId, [shown.Id], visible: true);

        var visibleOnly = await ListAsync(client, "visibleInStore=true");
        var hiddenOnly = await ListAsync(client, "visibleInStore=false");

        Assert.Equal(shown.Id, Assert.Single(visibleOnly.Items).Id);
        Assert.True(Assert.Single(visibleOnly.Items).VisibleInStore);
        Assert.Equal(hidden.Id, Assert.Single(hiddenOnly.Items).Id);

        var invalid = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products?visibleInStore=maybe",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task AnEditorWithTheOldVersionGets412AfterTheBatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var loaded = await ReadProductAsync(await CreateProductAsync(client, TenantId, "Vela", "V-1"));

        await SetVisibilityAsync(client, TenantId, [loaded.Id], visible: true);

        var stale = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{loaded.Id}",
            ProductBodies.Update(loaded, "Vela de coco", "V-1"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client, string tenantId, string name, string code) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            ProductBodies.Create(name, code),
            TestContext.Current.CancellationToken);

    private static async Task<ProductStoreVisibilityResult> SetVisibilityAsync(
        HttpClient client, string tenantId, Guid[] productIds, bool visible)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/store-visibility",
            new { productIds, visible },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ProductStoreVisibilityResult>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result;
    }

    private static async Task<ProductsResponse> ListAsync(HttpClient client, string query)
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

    private static async Task<bool> QueryVisibleAsync(NpgsqlConnection connection, Guid productId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT visible_in_store FROM catalog.products WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", productId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return (bool)value;
    }

    private static HttpClient CreateClient(QepApiFactory factory, string subjectId, string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', ManagePermissions));
        return client;
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
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

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Spec 2026-11-19 catalogo-productos-filtro-categoria: <c>GET /catalog/products?categoryId=</c> trae la
/// categoría y sus descendientes, con el total y la paginación del filtro, como <c>/pos/products</c>. Contra
/// Postgres porque lo que se prueba es la consulta: el <c>IN</c> sobre la columna convertida y el conteo.
///
/// El mundo: Bebidas → Gaseosas, y Panadería aparte. «Cola» en Gaseosas, «Agua» en Bebidas, «Pan» en
/// Panadería y «Suelto» sin categoría.
/// </summary>
public sealed class ProductCategoryFilterApiTests(SharedCatalogHost sharedHost) : IClassFixture<SharedCatalogHost>
{
    private string TenantId { get; } = Guid.CreateVersion7().ToString();
    private const string SubjectId = "01900000-0000-7000-8000-0000000000c1";
    private string OtherTenantId { get; } = Guid.CreateVersion7().ToString();
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000c2";

    private static readonly string[] All = [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    [Fact]
    public async Task CategoryFilterReturnsTheCategoryAndItsDescendantsOnly()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var world = await ArrangeAsync(client);

        var bebidas = await ListAsync(client, TenantId, $"?categoryId={world.Bebidas}");
        var gaseosas = await ListAsync(client, TenantId, $"?categoryId={world.Gaseosas}");
        var all = await ListAsync(client, TenantId, string.Empty);

        Assert.Equal(["Agua", "Cola"], bebidas.Items.Select(item => item.Name).Order());
        Assert.Equal(2, bebidas.Total);
        Assert.Equal(["Cola"], gaseosas.Items.Select(item => item.Name));
        Assert.Equal(1, gaseosas.Total);
        // Sin categoría no hay filtro: también el producto sin categoría y el de otra rama.
        Assert.Equal(4, all.Total);
    }

    [Fact]
    public async Task CategoryFilterTotalAndPagingCountTheFilteredProducts()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var world = await ArrangeAsync(client);
        await CreateProductAsync(client, "Té", "TE-01", world.Gaseosas);

        var first = await ListAsync(client, TenantId, $"?categoryId={world.Bebidas}&page=1&pageSize=2");
        var second = await ListAsync(client, TenantId, $"?categoryId={world.Bebidas}&page=2&pageSize=2");

        Assert.Equal((3, 1, 2), (first.Total, first.Page, first.PageSize));
        Assert.Equal(2, first.Items.Count);
        Assert.Equal((3, 2), (second.Total, second.Page));
        var last = Assert.Single(second.Items);
        Assert.Equal(
            ["Agua", "Cola", "Té"],
            first.Items.Concat(second.Items).Select(item => item.Name).Order());
        Assert.DoesNotContain(first.Items, item => item.Id == last.Id);
    }

    [Fact]
    public async Task CategoryFilterWithAnUnknownOrForeignCategoryGivesAnEmptyPage()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, All);
        await ArrangeAsync(client);
        var foreign = await CreateCategoryAsync(other, OtherTenantId, "Ajena", null);
        var foreignProduct = await other.PostAsJsonAsync(
            $"/api/v1/tenants/{OtherTenantId}/catalog/products",
            ProductBodies.Create("Producto ajeno", "AJ-01", categoryId: foreign),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, foreignProduct.StatusCode);

        var unknown = await ListAsync(client, TenantId, $"?categoryId={Guid.CreateVersion7()}");
        var foreignPage = await ListAsync(client, TenantId, $"?categoryId={foreign}&includeVariants=true");

        Assert.Empty(unknown.Items);
        Assert.Equal(0, unknown.Total);
        Assert.Empty(foreignPage.Items);
        Assert.Equal(0, foreignPage.Total);
    }

    [Fact]
    public async Task CategoryFilterCombinesWithSearchAndIncludeVariants()
    {
        var factory = await sharedHost.StartTestAsync(connectionString => new QepApiFactory(connectionString));
        using var client = CreateClient(factory, SubjectId, TenantId, All);
        var world = await ArrangeAsync(client);
        // Coincide con la búsqueda pero está fuera de la categoría.
        await CreateProductAsync(client, "Agua de panela", "AP-01", world.Panaderia);

        var searched = await ListAsync(client, TenantId, $"?categoryId={world.Bebidas}&search=agua");
        var withVariants = await ListAsync(client, TenantId, $"?categoryId={world.Gaseosas}&includeVariants=true");

        var agua = Assert.Single(searched.Items);
        Assert.Equal("Agua", agua.Name);
        Assert.Equal(1, searched.Total);

        var cola = Assert.Single(withVariants.Items);
        Assert.Equal("Cola", cola.Name);
        Assert.Equal(1, withVariants.Total);
        var variant = Assert.Single(cola.Variants!);
        Assert.Equal("CO-01", variant.Code);
    }

    private sealed record World(Guid Bebidas, Guid Gaseosas, Guid Panaderia);

    private async Task<World> ArrangeAsync(HttpClient client)
    {
        var bebidas = await CreateCategoryAsync(client, TenantId, "Bebidas", null);
        var gaseosas = await CreateCategoryAsync(client, TenantId, "Gaseosas", bebidas);
        var panaderia = await CreateCategoryAsync(client, TenantId, "Panadería", null);
        await CreateProductAsync(client, "Cola", "CO-01", gaseosas);
        await CreateProductAsync(client, "Agua", "AG-01", bebidas);
        await CreateProductAsync(client, "Pan", "PA-01", panaderia);
        await CreateProductAsync(client, "Suelto", "SU-01", null);
        return new World(bebidas, gaseosas, panaderia);
    }

    private async Task CreateProductAsync(HttpClient client, string name, string code, Guid? categoryId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            ProductBodies.Create(name, code, categoryId: categoryId),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<Guid> CreateCategoryAsync(HttpClient client, string tenantId, string name, Guid? parentId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/categories",
            new { name, parentId },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var category = await response.Content.ReadFromJsonAsync<CategoryResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(category);
        return category.Id;
    }

    private static async Task<ProductsResponse> ListAsync(HttpClient client, string tenantId, string query)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products{query}", TestContext.Current.CancellationToken);
        // El status se assertea acá: un 500 deserializaría con Items en null.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProductsResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static HttpClient CreateClient(
        QepApiFactory factory, string subjectId, string tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        return client;
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Pinned, never inherited: the developer's user-secrets may turn the seed on.
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Storage:R2:PublicBucket", "test-public-bucket");
            builder.UseSetting("Storage:R2:PublicBaseUrl", "https://cdn.qep.test");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}

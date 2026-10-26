using System.Net;
using System.Net.Http.Json;
using Modules.Pos.Application;
using Testcontainers.PostgreSql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// Las categorías de la caja y el filtro de la cuadrícula (spec 2026-10-26 §3.4), de punta a punta. Lo que
/// estas pruebas ven y las unitarias no: que el filtro por categoría de ProductRepository traduzca a SQL
/// contra Postgres (el <c>IN</c> sobre la columna convertida), que las descendientes salgan de la consulta
/// real, y que el cajero lea las dos rutas sin <c>catalog.product.read</c> (spec §3.5).
///
/// El mundo: Bebidas → Gaseosas. «Cola» en Gaseosas, «Agua» en Bebidas, «Vieja» en Gaseosas desactivada;
/// los tres productos de <see cref="PosWorld"/> (Shampoo, Avena, Jabón) quedan sin categoría.
/// </summary>
public sealed class PosCategoryApiTests
{
    [Fact]
    public async Task TheCashierListsCategoriesWithActiveRollupsAndFiltersTheGrid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var cashier = await InviteCashierAsync(factory, database, arranged.World.Tenant, CashierPermissions);

        var categories = await cashier.Client.GetAsync($"{arranged.World.Url}/categories", TestContext.Current.CancellationToken);
        var grid = await cashier.Client.GetAsync(
            $"{arranged.World.Url}/products?categoryId={arranged.Gaseosas}", TestContext.Current.CancellationToken);
        using var reader = CreateClient(
            factory, arranged.World.Tenant.OwnerUserId, arranged.World.Tenant.TenantId, PosPermissions.SaleRead);
        var forbidden = await reader.GetAsync($"{arranged.World.Url}/categories", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
        var list = await categories.Content.ReadFromJsonAsync<PosCategoriesResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(list);
        Assert.Equal((Guid?)null, list.Items.Single(item => item.Id == arranged.Bebidas).ParentId);
        Assert.Equal(2, list.Items.Single(item => item.Id == arranged.Bebidas).ProductCount);
        var gaseosas = list.Items.Single(item => item.Id == arranged.Gaseosas);
        Assert.Equal((arranged.Bebidas, 1), (gaseosas.ParentId, gaseosas.ProductCount));
        // Agua, Cola y los tres sin categoría; «Vieja», inactiva, no cuenta. La suma de las raíces daría 2.
        Assert.Equal(5, list.Total);

        Assert.Equal(HttpStatusCode.OK, grid.StatusCode);
        var page = await grid.Content.ReadFromJsonAsync<PosPage<PosCatalogProductResponse>>(TestContext.Current.CancellationToken);
        Assert.Equal(["Cola"], page!.Items.Select(item => item.Name));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task ProductsFilterByCategoryIncludesDescendantsAndCombinesWithSearch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var url = arranged.World.Url;

        var all = await GridAsync(arranged.World.Admin, $"{url}/products");
        var bebidas = await GridAsync(arranged.World.Admin, $"{url}/products?categoryId={arranged.Bebidas}");
        var gaseosas = await GridAsync(arranged.World.Admin, $"{url}/products?categoryId={arranged.Gaseosas}");
        var searched = await GridAsync(arranged.World.Admin, $"{url}/products?search=ag&categoryId={arranged.Bebidas}");

        // Sin categoría no hay filtro: los cinco activos, con y sin categoría.
        Assert.Equal(5, all.Total);
        Assert.Equal(2, bebidas.Total);
        Assert.Equal(["Agua", "Cola"], bebidas.Items.Select(item => item.Name).Order());
        Assert.DoesNotContain(bebidas.Items, item => item.ProductId == arranged.World.Jabon);
        Assert.Equal(["Cola"], gaseosas.Items.Select(item => item.Name));
        Assert.Equal(["Agua"], searched.Items.Select(item => item.Name));
        Assert.Equal(1, searched.Total);
    }

    [Fact]
    public async Task InactiveProductsNeitherCountNorAppearInTheFilteredGrid()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var tenant = arranged.World.Tenant;
        var vacia = await CreateCategoryAsync(tenant.Seeder, tenant.TenantId, "Vacía");

        var gaseosas = await GridAsync(arranged.World.Admin, $"{arranged.World.Url}/products?categoryId={arranged.Gaseosas}");
        var list = await arranged.World.Admin.GetFromJsonAsync<PosCategoriesResponse>(
            $"{arranged.World.Url}/categories", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(gaseosas.Items, item => item.ProductId == arranged.Vieja);
        Assert.Equal(1, gaseosas.Total);
        Assert.Equal(1, list!.Items.Single(item => item.Id == arranged.Gaseosas).ProductCount);
        var empty = list.Items.Single(item => item.Id == vacia);
        Assert.Equal(("Vacía", (Guid?)null, 0), (empty.Name, empty.ParentId, empty.ProductCount));
    }

    [Fact]
    public async Task AnUnknownOrForeignCategoryGivesAnEmptyPage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var arranged = await ArrangeAsync(factory, database);
        var other = await RegisterTenantAsync(factory);
        var foreign = await CreateCategoryAsync(other.Seeder, other.TenantId, "Ajena");
        await CreateProductAsync(other.Seeder, other.TenantId, "AJ-01", "Producto ajeno", 1_000m, null, categoryId: foreign);
        var url = arranged.World.Url;

        var unknown = await GridAsync(arranged.World.Admin, $"{url}/products?categoryId={Guid.CreateVersion7()}");
        var foreignPage = await GridAsync(arranged.World.Admin, $"{url}/products?categoryId={foreign}");
        var list = await arranged.World.Admin.GetFromJsonAsync<PosCategoriesResponse>(
            $"{url}/categories", TestContext.Current.CancellationToken);

        Assert.Empty(unknown.Items);
        Assert.Equal(0, unknown.Total);
        Assert.Empty(foreignPage.Items);
        Assert.Equal(0, foreignPage.Total);
        Assert.DoesNotContain(list!.Items, item => item.Id == foreign);
    }

    private sealed record CategoryWorld(PosWorld World, Guid Bebidas, Guid Gaseosas, Guid Cola, Guid Agua, Guid Vieja);

    private static async Task<CategoryWorld> ArrangeAsync(QepApiFactory factory, PostgreSqlContainer database)
    {
        var world = await PosWorld.ArrangeAsync(factory, database);
        var seeder = world.Tenant.Seeder;
        var tenantId = world.Tenant.TenantId;
        var bebidas = await CreateCategoryAsync(seeder, tenantId, "Bebidas");
        var gaseosas = await CreateCategoryAsync(seeder, tenantId, "Gaseosas", bebidas);
        var cola = await CreateProductAsync(seeder, tenantId, "CO-01", "Cola", 3_500m, null, categoryId: gaseosas);
        var agua = await CreateProductAsync(seeder, tenantId, "AG-01", "Agua", 2_000m, null, categoryId: bebidas);
        var vieja = await CreateProductAsync(seeder, tenantId, "VI-01", "Vieja", 1_500m, null, categoryId: gaseosas);
        var deactivated = await seeder.PostAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{vieja}/deactivate", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        return new CategoryWorld(world, bebidas, gaseosas, cola, agua, vieja);
    }

    private static async Task<PosPage<PosCatalogProductResponse>> GridAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PosPage<PosCatalogProductResponse>>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }
}

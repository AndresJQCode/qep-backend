using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Domain.Pricing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 §7: importar, ver la importación, la cobertura y desvincular, de punta a punta con el host entero (políticas,
/// validador, mapeo de errores) contra Postgres. Shopify es un <see cref="IShopifyProductsSource"/> falso; el worker no corre solo.
/// </summary>
public sealed class ShopifyProductsApiTests
{
    private const string Scopes = "read_orders,read_customers,read_products";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string StoreRoute(ShopifyWorld world) =>
        $"/api/v1/tenants/{world.TenantId}/shopify/stores/{world.ConnectionId}/products";

    private static HttpClient ClientOf(QepApiFactory factory, ShopifyWorld world) =>
        CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, ShopifyPermissions.ConnectionRead, ShopifyPermissions.ConnectionManage);

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();

    private static async Task<(QepApiFactory Factory, ShopifyWorld World)> ArrangeAsync(string connectionString, VariantKeysSource? source = null)
    {
        var factory = new QepApiFactory(connectionString)
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IShopifyProductsSource>();
                services.AddSingleton<IShopifyProductsSource>(source ?? new VariantKeysSource([]));
            },
        };
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString, Scopes);
        await world.ConfigureStoreAsync(factory);
        return (factory, world);
    }

    [Fact]
    public async Task PostImportReturns202AndTheGetShowsItPending()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (factory, world) = await ArrangeAsync(connectionString);
        using var _ = factory;
        using var client = ClientOf(factory, world);

        var posted = await client.PostAsJsonAsync($"{StoreRoute(world)}/import", new { mode = "all" }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var importId = (await posted.Content.ReadFromJsonAsync<JsonObject>(Ct))!["importId"]!.GetValue<Guid>();
        var current = (await client.GetFromJsonAsync<JsonObject>($"{StoreRoute(world)}/import", Ct))!;
        Assert.Equal(importId, current["id"]!.GetValue<Guid>());
        Assert.Equal(("All", "Pending"), (current["mode"]!.GetValue<string>(), current["status"]!.GetValue<string>()));
        Assert.Empty(current["issues"]!.AsArray());
        Assert.Equal("shopify.products.import_requested|shopify_product_import", await ScalarAsync<string>(
            connectionString,
            "SELECT action || '|' || resource_type FROM audit.entries WHERE tenant_id = @t AND resource_id = @r",
            ("t", world.TenantId), ("r", importId.ToString())));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task ASecondPostWhileOneIsLiveIs409ImportRunning()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (factory, world) = await ArrangeAsync(connectionString);
        using var _ = factory;
        using var client = ClientOf(factory, world);
        (await client.PostAsJsonAsync($"{StoreRoute(world)}/import", new { mode = "All" }, Ct)).EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync($"{StoreRoute(world)}/import", new { mode = "Missing" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ShopifyErrorCodes.ProductsImportRunning, await CodeAsync(second));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task GetReturnsNullWithoutImports()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (factory, world) = await ArrangeAsync(connectionString);
        using var _ = factory;
        using var client = ClientOf(factory, world);

        var response = await client.GetAsync($"{StoreRoute(world)}/import", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("null", (await response.Content.ReadAsStringAsync(Ct)).Trim());
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task TheGetReturnsAtMost50Issues()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (factory, world) = await ArrangeAsync(connectionString);
        using var _ = factory;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var imports = scope.ServiceProvider.GetRequiredService<IShopifyProductImportRepository>();
            var now = DateTimeOffset.UtcNow;
            var import = ShopifyProductImport.Request(
                world.TenantId, world.ConnectionId, ShopifyProductImportMode.All, world.AdvisorMembershipId, world.Tenant.OwnerUserId, now);
            imports.Add(import);
            for (var index = 0; index < 60; index++)
            {
                imports.AddIssue(ShopifyProductImportIssue.Create(import.Id, index, $"Producto {index}", ShopifyProductCodes.PartialMatch, now.AddSeconds(index)));
            }

            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var client = ClientOf(factory, world);
        var current = (await client.GetFromJsonAsync<JsonObject>($"{StoreRoute(world)}/import", Ct))!;

        var issues = current["issues"]!.AsArray();
        Assert.Equal(50, issues.Count);
        Assert.Equal((0L, "Producto 0", "partial_match"), (
            issues[0]!["shopifyProductId"]!.GetValue<long>(), issues[0]!["title"]!.GetValue<string>(), issues[0]!["code"]!.GetValue<string>()));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task CoverageCountsLinkedAndSkuMatchedVariants()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var source = new VariantKeysSource(
        [
            new ShopifyVariantKey(901L, "VINCULADA-1", "Camiseta vinculada"),
            new ShopifyVariantKey(902L, " CAM-001 ", "Camiseta"),
            new ShopifyVariantKey(903L, "NO-EXISTE-1", "Taza"),
        ]);
        var (factory, world) = await ArrangeAsync(connectionString, source);
        using var _ = factory;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IShopifyProductLinkRepository>().Add(ShopifyProductLink.Create(
                world.TenantId, world.ConnectionId, 90L, 901L, world.Gorra, world.Gorra, ShopifyProductLinkOrigin.MatchedBySku, null, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var client = ClientOf(factory, world);
        var coverage = (await client.GetFromJsonAsync<JsonObject>($"{StoreRoute(world)}/coverage", Ct))!;

        Assert.Equal((3, 2, false), (
            coverage["shopifyVariants"]!.GetValue<int>(), coverage["matched"]!.GetValue<int>(), coverage["catalogEmpty"]!.GetValue<bool>()));
        var missing = Assert.Single(coverage["missingSample"]!.AsArray())!;
        Assert.Equal(("NO-EXISTE-1", "Taza"), (missing["sku"]!.GetValue<string>(), missing["title"]!.GetValue<string>()));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task UnlinkReleasesTheProductAndWebhooksIgnoreIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (factory, world) = await ArrangeAsync(connectionString);
        using var _ = factory;
        var context = new ShopifyProductSyncContext(
            new ShopifyConnectionRef(world.TenantId, world.ConnectionId, "954889", Shop, true, "shpat_x"),
            new ShopifyPricingContext("COP", true, true, new PriceRounding(0, PriceRoundingMode.Nearest), new ShopifyDefaultTaxRates(null, null)),
            Guid.Empty,
            ShopifySyncTrigger.Webhook);
        var updatedAt = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        ShopifyProductSnapshot Snapshot(DateTimeOffset at) =>
            new(7001L, "Termo", ShopifyProductStatus.Active, at, [], [new ShopifyVariantSnapshot(70011L, "TERMO-1", null, 40_000m, true, [])], null);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ShopifyProductSync>().ApplyAsync(context, Snapshot(updatedAt), Ct);
        }

        var productId = await ScalarAsync<Guid>(
            connectionString, "SELECT product_id FROM shopify.product_links WHERE connection_id = @c LIMIT 1", ("c", world.ConnectionId));
        using var client = ClientOf(factory, world);

        var unlinked = await client.DeleteAsync($"/api/v1/tenants/{world.TenantId}/shopify/products/{productId}/link", Ct);

        Assert.Equal(HttpStatusCode.NoContent, unlinked.StatusCode);
        Assert.True(await ScalarAsync<bool>(
            connectionString, "SELECT external_source IS NULL FROM catalog.products WHERE id = @p", ("p", productId)));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.product_links WHERE product_id = @p", ("p", productId)));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'shopify.products.unlinked' AND resource_id = @r",
            ("t", world.TenantId), ("r", productId.ToString())));

        // El producto liberado ya no es de Shopify: el siguiente webhook no lo toca. Su SKU es de un producto de QEP sin vínculos,
        // así que el webhook lo vincula por SKU (S-2) y no pisa nada.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<ShopifyProductSync>()
                .ApplyAsync(context, Snapshot(updatedAt.AddMinutes(5)) with { Title = "Termo renombrado" }, Ct);
            Assert.NotEqual(ShopifyProductOutcomeKind.Updated, outcome.Kind);
        }

        Assert.Equal("Termo", await ScalarAsync<string>(connectionString, "SELECT name FROM catalog.products WHERE id = @p", ("p", productId)));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task UnlinkOfAnUnlinkedProductIs404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (factory, world) = await ArrangeAsync(connectionString);
        using var _ = factory;
        using var client = ClientOf(factory, world);

        var response = await client.DeleteAsync($"/api/v1/tenants/{world.TenantId}/shopify/products/{world.Camiseta}/link", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ShopifyErrorCodes.ProductsLinkNotFound, await CodeAsync(response));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task AnotherTenantGets403NotFound()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var (factory, world) = await ArrangeAsync(connectionString);
        using var _ = factory;
        var otherTenant = Guid.CreateVersion7();
        using var client = CreateClient(
            factory, world.Tenant.OwnerUserId, otherTenant, ShopifyPermissions.ConnectionRead, ShopifyPermissions.ConnectionManage);

        var import = await client.PostAsJsonAsync($"{StoreRoute(world)}/import", new { mode = "All" }, Ct);
        var unlink = await client.DeleteAsync($"/api/v1/tenants/{world.TenantId}/shopify/products/{world.Camiseta}/link", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);
        Assert.Equal("authorization.denied", await CodeAsync(import));
        Assert.Equal(HttpStatusCode.Forbidden, unlink.StatusCode);
        Assert.Equal("authorization.denied", await CodeAsync(unlink));
        world.Tenant.Seeder.Dispose();
    }

    /// <summary>Shopify falso para la cobertura: todas las variantes en una página; la tienda en COP.</summary>
    private sealed class VariantKeysSource(IReadOnlyList<ShopifyVariantKey> variants) : IShopifyProductsSource
    {
        public Task<ShopifyShopInfo> ReadShopAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken) =>
            Task.FromResult(new ShopifyShopInfo("COP", true, 0));

        public Task<ShopifyProductsPage> FetchProductsAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(new ShopifyProductsPage([], null));

        public Task<ShopifyProductSnapshot?> ReadProductAsync(ShopifyConnectionRef connection, long productId, CancellationToken cancellationToken) =>
            Task.FromResult<ShopifyProductSnapshot?>(null);

        public Task<ShopifyVariantKeysPage> ListVariantKeysAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(new ShopifyVariantKeysPage(variants, null));
    }
}

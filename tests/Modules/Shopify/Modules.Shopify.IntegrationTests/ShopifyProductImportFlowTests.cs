using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Workers;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 §5.2 contra Postgres y el Catalog real: el worker reclama la importación, recorre las páginas de un Shopify falso
/// (<see cref="IShopifyProductsSource"/>), crea los productos y los cuenta; un rechazo de Catalog deshace sólo ese producto (Review
/// Focus 3) y un token vencido termina la importación. El worker no corre solo: la prueba llama <c>DrainAsync</c>.
/// </summary>
public sealed class ShopifyProductImportFlowTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyProductSnapshot Product(long id, string? title = null) =>
        new(
            id,
            title ?? $"Producto importado {id}",
            ShopifyProductStatus.Active,
            UpdatedAt,
            [],
            [new ShopifyVariantSnapshot(id * 10, $"IMP-{id}", null, 25_000m, true, [])],
            null);

    private static async Task<(ShopifyWorld World, Guid ImportId)> ArrangeAsync(QepApiFactory factory, string connectionString)
    {
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString, scopes: "read_orders,read_customers,read_products");
        await world.ConfigureStoreAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var import = ShopifyProductImport.Request(
            world.TenantId, world.ConnectionId, ShopifyProductImportMode.All, world.AdvisorMembershipId, world.Tenant.OwnerUserId, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IShopifyProductImportRepository>().Add(import);
        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        return (world, import.Id);
    }

    private static QepApiFactory Factory(string connectionString, PagedSource source) =>
        new(connectionString)
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IShopifyProductsSource>();
                services.AddSingleton<IShopifyProductsSource>(source);
            },
        };

    private static Task DrainAsync(QepApiFactory factory) =>
        ActivatorUtilities.CreateInstance<ShopifyProductImportWorker>(factory.Services).DrainAsync(Ct);

    [Fact]
    public async Task AnImportOfTwoPagesCreatesTheProductsAndCountsThem()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var source = new PagedSource([.. Enumerable.Range(1, 12).Select(index => Product(index))]);
        using var factory = Factory(connectionString, source);
        var (world, importId) = await ArrangeAsync(factory, connectionString);

        await DrainAsync(factory);

        Assert.Equal("Done|12|12|12|0|0|1", await ScalarAsync<string>(
            connectionString,
            "SELECT status || '|' || total || '|' || done || '|' || created || '|' || matched || '|' || skipped || '|' || attempts FROM shopify.product_imports WHERE id = @id",
            ("id", importId)));
        Assert.Equal(12L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM catalog.products WHERE tenant_id = @t AND external_source = 'shopify'", ("t", world.TenantId)));
        Assert.True(await ScalarAsync<bool>(
            connectionString, "SELECT products_synced_at IS NOT NULL FROM shopify.stores WHERE connection_id = @c", ("c", world.ConnectionId)));
        Assert.Equal([null, "page-2"], source.Cursors);
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task ACatalogRejectionRollsBackAndTheImportGoesOn()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var source = new PagedSource([Product(1), Product(2, new string('x', 201)), Product(3)]);
        using var factory = Factory(connectionString, source);
        var (world, importId) = await ArrangeAsync(factory, connectionString);

        await DrainAsync(factory);

        Assert.Equal("Done|3|2|1", await ScalarAsync<string>(
            connectionString,
            "SELECT status || '|' || done || '|' || created || '|' || skipped FROM shopify.product_imports WHERE id = @id",
            ("id", importId)));
        Assert.Equal("2|catalog.product.name_too_long", await ScalarAsync<string>(
            connectionString,
            "SELECT shopify_product_id || '|' || code FROM shopify.product_import_issues WHERE import_id = @id",
            ("id", importId)));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.product_links WHERE connection_id = @c AND shopify_product_id = 2", ("c", world.ConnectionId)));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM catalog.products WHERE tenant_id = @t AND external_source = 'shopify'", ("t", world.TenantId)));
        world.Tenant.Seeder.Dispose();
    }

    [Fact]
    public async Task AnExpiredTokenFailsTheImport()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var source = new PagedSource([Product(1)])
        {
            Failure = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.Rejected, null),
        };
        using var factory = Factory(connectionString, source);
        var (world, importId) = await ArrangeAsync(factory, connectionString);

        await DrainAsync(factory);

        Assert.Equal("Failed|token_expired|true", await ScalarAsync<string>(
            connectionString,
            "SELECT status || '|' || failure_code || '|' || (finished_at IS NOT NULL)::text FROM shopify.product_imports WHERE id = @id",
            ("id", importId)));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM catalog.products WHERE tenant_id = @t AND external_source = 'shopify'", ("t", world.TenantId)));
        world.Tenant.Seeder.Dispose();
    }

    /// <summary>Shopify falso: la tienda en COP con IVA incluido y los productos en páginas de 10; los cursores son
    /// <c>page-2</c>, <c>page-3</c>… <see cref="Failure"/> se lanza en cada página.</summary>
    private sealed class PagedSource(IReadOnlyList<ShopifyProductSnapshot> products) : IShopifyProductsSource
    {
        public ShopifyProductsSourceException? Failure { get; init; }

        public List<string?> Cursors { get; } = [];

        public Task<ShopifyShopInfo> ReadShopAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken) =>
            Task.FromResult(new ShopifyShopInfo("COP", true, products.Count));

        public Task<ShopifyProductsPage> FetchProductsAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken)
        {
            lock (Cursors)
            {
                Cursors.Add(cursor);
            }

            if (Failure is { } failure)
            {
                return Task.FromException<ShopifyProductsPage>(failure);
            }

            var page = cursor is null ? 1 : int.Parse(cursor["page-".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var slice = products.Skip((page - 1) * 10).Take(10).ToArray();
            var next = page * 10 < products.Count ? $"page-{page + 1}" : null;
            return Task.FromResult(new ShopifyProductsPage(slice, next));
        }

        public Task<ShopifyProductSnapshot?> ReadProductAsync(ShopifyConnectionRef connection, long productId, CancellationToken cancellationToken) =>
            Task.FromResult(products.FirstOrDefault(product => product.Id == productId));

        public Task<ShopifyVariantKeysPage> ListVariantKeysAsync(ShopifyConnectionRef connection, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(new ShopifyVariantKeysPage(
                [.. products.SelectMany(product => product.Variants.Select(variant => new ShopifyVariantKey(variant.Id, variant.Sku, product.Title)))],
                null));
    }
}

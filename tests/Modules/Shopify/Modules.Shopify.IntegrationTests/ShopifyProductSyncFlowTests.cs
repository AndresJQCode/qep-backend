using BuildingBlocks.Domain.Pricing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Shopify.Application;
using Modules.Storage.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 §5.3 y §6 contra Postgres y el Catalog real: el candado de producto y el índice único dejan un solo producto
/// aunque el webhook y la importación lleguen a la vez (Review Focus 1), los vínculos a un producto borrado a mano se reemplazan
/// (Review Focus 4) y la imagen destacada entra a la galería por la subida de servidor de Storage. Host propio por prueba.
/// </summary>
public sealed class ShopifyProductSyncFlowTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    // Un PNG de 1×1 que decodifica (Storage pone en cuarentena lo que ImageSharp no lee).
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyProductSyncContext Context(Guid tenantId, Guid connectionId) =>
        new(
            new ShopifyConnectionRef(tenantId, connectionId, "954889", "mi-tienda.myshopify.com", true, "shpat_x"),
            new ShopifyPricingContext("COP", true, true, new PriceRounding(0, PriceRoundingMode.Nearest), new ShopifyDefaultTaxRates(null, null)),
            Guid.Empty,
            ShopifySyncTrigger.Webhook);

    private static ShopifyProductSnapshot Product(DateTimeOffset updatedAt, ShopifyProductImage? image = null) =>
        new(
            7001L,
            "Camiseta",
            ShopifyProductStatus.Active,
            updatedAt,
            [new ShopifyProductOption("Talla", ["S", "M"])],
            [
                new ShopifyVariantSnapshot(4455L, "CAM-S", null, 50_000m, true, ["S"]),
                new ShopifyVariantSnapshot(4456L, "CAM-M", null, 50_000m, true, ["M"]),
            ],
            image);

    private static async Task<ShopifyProductOutcome> ApplyAsync(QepApiFactory factory, ShopifyProductSyncContext context, ShopifyProductSnapshot product)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ShopifyProductSync>().ApplyAsync(context, product, Ct);
    }

    [Fact]
    public async Task AWebhookAndTheImportOfTheSameProductLeaveOneProduct()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, connectionId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var context = Context(tenantId, connectionId);

        var outcomes = await Task.WhenAll(
            ApplyAsync(factory, context, Product(UpdatedAt)),
            ApplyAsync(factory, context with { Trigger = ShopifySyncTrigger.Import }, Product(UpdatedAt)));

        Assert.Single(outcomes, outcome => outcome.Kind == ShopifyProductOutcomeKind.Created);
        Assert.Single(outcomes, outcome => outcome.Kind == ShopifyProductOutcomeKind.Unchanged);
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM catalog.products WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.product_links WHERE connection_id = @c", ("c", connectionId)));
    }

    [Fact]
    public async Task StaleLinksToADeletedProductAreReplaced()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, connectionId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var context = Context(tenantId, connectionId);
        await ApplyAsync(factory, context, Product(UpdatedAt));
        var first = await ScalarAsync<Guid>(
            connectionString, "SELECT product_id FROM shopify.product_links WHERE connection_id = @c LIMIT 1", ("c", connectionId));

        // El asesor lo borró a mano en QEP: los vínculos quedan colgando (sin FK a catalog).
        await ExecuteAsync(connectionString, "DELETE FROM catalog.products WHERE id = @id", ("id", first));
        var outcome = await ApplyAsync(factory, context, Product(UpdatedAt.AddMinutes(5)));

        Assert.Equal(ShopifyProductOutcomeKind.Created, outcome.Kind);
        var second = await ScalarAsync<Guid>(
            connectionString, "SELECT product_id FROM shopify.product_links WHERE connection_id = @c LIMIT 1", ("c", connectionId));
        Assert.NotEqual(first, second);
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.product_links WHERE connection_id = @c AND product_id = @p",
            ("c", connectionId), ("p", first)));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.product_links WHERE connection_id = @c", ("c", connectionId)));
    }

    [Fact]
    public async Task TheImportedImageLandsInTheGallery()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var storage = new InMemoryObjectStorage();
        using var factory = new QepApiFactory(connectionString)
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(storage);
                services.RemoveAll<IShopifyImageFetcher>();
                services.AddSingleton<IShopifyImageFetcher>(new FixedImageFetcher(new ShopifyFetchedImage(TinyPng, "image/png")));
            },
        };
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = Guid.CreateVersion7();

        var outcome = await ApplyAsync(
            factory,
            Context(tenant.TenantId, connectionId),
            Product(UpdatedAt, new ShopifyProductImage(9L, new Uri("https://cdn.shopify.com/s/files/camiseta.png"))));

        Assert.Equal(ShopifyProductOutcomeKind.Created, outcome.Kind);
        Assert.Empty(outcome.IssueCodes);
        var productId = await ScalarAsync<Guid>(
            connectionString, "SELECT product_id FROM shopify.product_links WHERE connection_id = @c LIMIT 1", ("c", connectionId));
        var fileId = await ScalarAsync<Guid>(
            connectionString,
            "SELECT file_id FROM catalog.product_gallery_images WHERE product_id = @p ORDER BY position LIMIT 1",
            ("p", productId));
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM shopify.product_links WHERE connection_id = @c AND image_file_id = @f AND shopify_image_id = 9",
            ("c", connectionId), ("f", fileId)));
        Assert.Equal("Available", await ScalarAsync<string>(
            connectionString, "SELECT status FROM storage.file_resources WHERE id = @f", ("f", fileId)));
        tenant.Seeder.Dispose();
    }

    private sealed class FixedImageFetcher(ShopifyFetchedImage image) : IShopifyImageFetcher
    {
        public Task<ShopifyFetchedImage?> FetchAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult<ShopifyFetchedImage?>(image);
    }
}

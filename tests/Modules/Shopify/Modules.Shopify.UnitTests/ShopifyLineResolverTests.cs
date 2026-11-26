using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ImporterWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 §4: una línea de producto con id de variante busca primero el vínculo de la tienda; el SKU queda para las que no
/// tienen vínculo. Con vínculo, la variante de QEP vinculada decide, también si está inactiva.
/// </summary>
public sealed class ShopifyLineResolverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyLine LineOf(string? sku, long? variantId) => new(sku, 1m, 10_000m, 0m, "Gorra", false, 9001, 1m, variantId);

    private static ShopifyLineResolver ResolverOf(ImporterWorld world) => new(world.Products, world.CustomLines, world.ProductLinks);

    private static Guid LinkTo(ImporterWorld world, long shopifyVariantId, bool active = true)
    {
        var variant = Guid.CreateVersion7();
        world.Products.WithVariant(variant, taxPercentage: 19, active: active);
        world.ProductLinks.Link(TenantId, ConnectionId, 700L, shopifyVariantId, world.Products.ByVariant[variant].ProductId, variant);
        return variant;
    }

    [Fact]
    public async Task ALinkedVariantWinsOverTheSku()
    {
        var world = new ImporterWorld();
        var linked = LinkTo(world, 4455L);

        var (lines, unmatched, _) = await ResolverOf(world).ResolveAsync(
            TenantId, ConnectionId, Order(lines: [LineOf("CAM-001", 4455L)]), "COP", true, Ct);

        Assert.Equal(linked, Assert.Single(lines).VariantId);
        Assert.NotEqual(world.Products.Products["CAM-001"].VariantId, lines[0].VariantId);
        Assert.Empty(unmatched);
        // El SKU no se consulta: la línea ya casó por su vínculo.
        Assert.Empty(world.Products.Requests);
        Assert.Equal([4455L], Assert.Single(world.ProductLinks.VariantRequests));
    }

    [Fact]
    public async Task ALinkedButInactiveVariantIsUnmatchedWithoutFallingBackToTheSku()
    {
        var world = new ImporterWorld();
        LinkTo(world, 4455L, active: false);

        var (lines, unmatched, _) = await ResolverOf(world).ResolveAsync(
            TenantId, ConnectionId, Order(lines: [LineOf("CAM-001", 4455L)]), "COP", true, Ct);

        Assert.Empty(lines);
        Assert.Equal(["CAM-001"], unmatched);
    }

    [Fact]
    public async Task ALineWithoutSkuMatchesByItsLink()
    {
        var world = new ImporterWorld();
        var linked = LinkTo(world, 4455L);

        var (lines, unmatched, _) = await ResolverOf(world).ResolveAsync(
            TenantId, ConnectionId, Order(lines: [LineOf(null, 4455L)]), "COP", true, Ct);

        Assert.Equal(linked, Assert.Single(lines).VariantId);
        Assert.Empty(unmatched);
    }

    [Fact]
    public async Task AVariantWithoutLinkStillMatchesBySku()
    {
        var world = new ImporterWorld();

        var (lines, _, _) = await ResolverOf(world).ResolveAsync(
            TenantId, ConnectionId, Order(lines: [LineOf("CAM-001", 9999L)]), "COP", true, Ct);

        Assert.Equal(world.Products.Products["CAM-001"].VariantId, Assert.Single(lines).VariantId);
    }

    // Review Focus 4: el producto vinculado se borró a mano en QEP; el vínculo cuenta como ausente y la línea va por SKU.
    [Fact]
    public async Task ALinkWhoseVariantNoLongerExistsFallsBackToTheSku()
    {
        var world = new ImporterWorld();
        world.ProductLinks.Link(TenantId, ConnectionId, 700L, 4455L, Guid.CreateVersion7(), Guid.CreateVersion7());

        var (lines, _, _) = await ResolverOf(world).ResolveAsync(
            TenantId, ConnectionId, Order(lines: [LineOf("CAM-001", 4455L)]), "COP", true, Ct);

        Assert.Equal(world.Products.Products["CAM-001"].VariantId, Assert.Single(lines).VariantId);
    }

    [Fact]
    public async Task ALinkOfAnotherStoreIsNotUsed()
    {
        var world = new ImporterWorld();
        var variant = Guid.CreateVersion7();
        world.Products.WithVariant(variant);
        world.ProductLinks.Link(TenantId, Guid.CreateVersion7(), 700L, 4455L, world.Products.ByVariant[variant].ProductId, variant);

        var (lines, _, _) = await ResolverOf(world).ResolveAsync(
            TenantId, ConnectionId, Order(lines: [LineOf("CAM-001", 4455L)]), "COP", true, Ct);

        Assert.Equal(world.Products.Products["CAM-001"].VariantId, Assert.Single(lines).VariantId);
    }

    [Fact]
    public async Task OrderImportUsesTheLink()
    {
        var world = new ImporterWorld();
        var linked = LinkTo(world, 4455L);
        var snapshot = Order(lines: [LineOf(null, 4455L)]);

        var outcome = await world.ImportAsync(world.RowFor(snapshot), snapshot);

        Assert.Equal(ShopifyImportState.Imported, outcome.State);
        Assert.Equal(linked, Assert.Single(Assert.Single(world.Writer.Creates).Item1.Lines).VariantId);
    }
}

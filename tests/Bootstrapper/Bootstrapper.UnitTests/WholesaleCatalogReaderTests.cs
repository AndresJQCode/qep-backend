using Modules.Catalog.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-19: el lector de Catalog hacia Wholesale entrega con cada variante vendible su producto y su foto, con la regla
/// de las variantes vendibles de <c>GET /catalog/products?includeVariants=true</c> y el re-chequeo de tenant del mayorista.
/// </summary>
public sealed class WholesaleCatalogReaderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WholesaleCatalogReader Reader(VariantWorld world, FakeProductImageLookup images) =>
        new(
            new FakeProductCategoryRepository(),
            new FakeProductRepository(world.Product),
            new FakeOptionDefinitionRepository(world.Size),
            new FakeTaxRateRepository(),
            images);

    private static ProductImageRef Available(Guid fileId, string url, Guid? tenantId = null) =>
        new(fileId, tenantId ?? TenantId, "image/png", true, url);

    [Fact]
    public async Task ItemsCarryProductAndVariantPhotoElseProductPhoto()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var images = new FakeProductImageLookup(
            Available(world.MainFileId, "https://cdn.example.co/main.png"),
            Available(world.MediumFileId, "https://cdn.example.co/m.png"));

        var (items, total) = await Reader(world, images).SearchSellableAsync(TenantId, "COP", null, null, 1, 50, Ct);

        Assert.Equal(1, total);
        var medium = items.Single(item => item.VariantId == world.Medium.Id.Value);
        var large = items.Single(item => item.VariantId == world.Large.Id.Value);
        Assert.Equal(
            (world.Product.Id.Value, "Camiseta básica", "https://cdn.example.co/m.png"),
            (medium.ProductId, medium.ProductName, medium.ImageUrl));
        Assert.Equal(
            (world.Product.Id.Value, "Camiseta básica", "https://cdn.example.co/main.png"),
            (large.ProductId, large.ProductName, large.ImageUrl));
        Assert.Equal(1, images.Calls);
    }

    [Fact]
    public async Task ItemsWithoutAnyAvailablePhotoHaveNullUrl()
    {
        var world = VariantWorld.Create(TenantId, Now);

        var (items, _) = await Reader(world, new FakeProductImageLookup())
            .SearchSellableAsync(TenantId, "COP", null, null, 1, 50, Ct);

        Assert.All(items, item => Assert.Null(item.ImageUrl));
    }

    // El comprador nunca recibe la URL de un archivo que no es del mayorista, ni uno que no terminó de subirse.
    [Fact]
    public async Task APhotoOfAnotherTenantOrNotAvailableIsNeverReturned()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var images = new FakeProductImageLookup(
            Available(world.MainFileId, "https://cdn.example.co/other-tenant.png", Guid.CreateVersion7()),
            new ProductImageRef(world.MediumFileId, TenantId, "image/png", false, "https://cdn.example.co/pending.png"));

        var (items, _) = await Reader(world, images).SearchSellableAsync(TenantId, "COP", null, null, 1, 50, Ct);

        Assert.All(items, item => Assert.Null(item.ImageUrl));
    }

    [Fact]
    public async Task FindSellableAlsoCarriesProductAndPhoto()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var images = new FakeProductImageLookup(Available(world.MediumFileId, "https://cdn.example.co/m.png"));

        var found = await Reader(world, images).FindSellableAsync(TenantId, "COP", [world.Medium.Id.Value], Ct);

        var medium = Assert.Single(found).Value;
        Assert.Equal(
            (world.Product.Id.Value, "Camiseta básica", "https://cdn.example.co/m.png"),
            (medium.ProductId, medium.ProductName, medium.ImageUrl));
    }
}

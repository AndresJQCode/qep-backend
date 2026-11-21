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

        var (items, total) = await Reader(world, images).SearchSellableAsync(TenantId, "COP", null, null, null, null, 1, 50, Ct);

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
            .SearchSellableAsync(TenantId, "COP", null, null, null, null, 1, 50, Ct);

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

        var (items, _) = await Reader(world, images).SearchSellableAsync(TenantId, "COP", null, null, null, null, 1, 50, Ct);

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

    // Spec 2026-11-20: exclusions per price list.
    private static (WholesaleCatalogReader Reader, FakeProductRepository Products, Guid Parent, Guid Child, Guid Other) ReaderWithTree(
        VariantWorld world, FakeProductImageLookup images)
    {
        var parent = Modules.Catalog.Domain.ProductCategory.Create(Modules.Catalog.Domain.ProductCategoryId.New(), TenantId, "Hogar", null, Now);
        var child = Modules.Catalog.Domain.ProductCategory.Create(Modules.Catalog.Domain.ProductCategoryId.New(), TenantId, "Cocina", parent.Id, Now);
        var other = Modules.Catalog.Domain.ProductCategory.Create(Modules.Catalog.Domain.ProductCategoryId.New(), TenantId, "Ropa", null, Now);
        var products = new FakeProductRepository(world.Product);
        var reader = new WholesaleCatalogReader(
            new FakeProductCategoryRepository((parent, 0), (child, 0), (other, 0)),
            products,
            new FakeOptionDefinitionRepository(world.Size),
            new FakeTaxRateRepository(),
            images);
        return (reader, products, parent.Id.Value, child.Id.Value, other.Id.Value);
    }

    [Fact]
    public async Task ExcludedCategoriesAreExpandedToTheirDescendantsAndTravelWithTheExcludedProducts()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var (reader, products, parent, child, _) = ReaderWithTree(world, new FakeProductImageLookup());
        var hiddenProduct = Guid.CreateVersion7();

        await reader.SearchSellableAsync(TenantId, "COP", null, [parent], [hiddenProduct], null, 1, 50, Ct);
        var searchExclusions = products.LastExclusions!.Value;
        await reader.SampleAsync(TenantId, "COP", null, [parent], [hiddenProduct], 20, Ct);
        var sampleExclusions = products.LastExclusions!.Value;

        Assert.Null(products.LastCategoryIds);
        foreach (var sent in new[] { searchExclusions, sampleExclusions })
        {
            Assert.Equal(new[] { parent, child }.Order(), sent.Categories!.Order());
            Assert.Equal([hiddenProduct], sent.Products);
        }
    }

    [Fact]
    public async Task NoExclusionsSendNothingToTheRepository()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var (reader, products, _, _, _) = ReaderWithTree(world, new FakeProductImageLookup());

        await reader.SearchSellableAsync(TenantId, "COP", null, null, null, null, 1, 50, Ct);

        var (categories, hidden) = products.LastExclusions!.Value;
        Assert.True(categories is null or { Count: 0 });
        Assert.True(hidden is null or { Count: 0 });
    }

    [Fact]
    public async Task FindProductsCarriesTheCodeOnlyForASingleVariantAndTheVariantCount()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var (reader, _, _, _, _) = ReaderWithTree(world, new FakeProductImageLookup());

        var found = await reader.FindProductsAsync(TenantId, [world.Product.Id.Value, Guid.CreateVersion7()], Ct);

        var product = Assert.Single(found).Value;
        Assert.Equal((world.Product.Id.Value, "Camiseta básica", null, 2), (product.Id, product.Name, product.Code, product.VariantCount));
    }

    [Fact]
    public async Task SearchProductsMapsRowsWithTheMainPhotoAndExpandsTheCategory()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var images = new FakeProductImageLookup(Available(world.MainFileId, "https://cdn.example.co/main.png"));
        var (reader, products, parent, child, _) = ReaderWithTree(world, images);

        var (items, total) = await reader.SearchProductsAsync(TenantId, "COP", parent, " cam ", 1, 50, Ct);

        var row = Assert.Single(items);
        Assert.Equal((1, world.Product.Id.Value, "https://cdn.example.co/main.png", 2), (total, row.Id, row.ImageUrl, row.VariantCount));
        Assert.Equal(new[] { parent, child }.Order(), products.LastCategoryIds!.Order());
    }

    [Fact]
    public async Task FindProductsNeverReturnsAProductOfAnotherTenant()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var (reader, _, _, _, _) = ReaderWithTree(world, new FakeProductImageLookup());

        var found = await reader.FindProductsAsync(Guid.CreateVersion7(), [world.Product.Id.Value], Ct);

        Assert.Empty(found);
    }

    [Fact]
    public async Task SearchProductsAsksTheRepositoryForNameOrderAndTheBuyerCatalogDoesNot()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var (reader, products, _, _, _) = ReaderWithTree(world, new FakeProductImageLookup());

        await reader.SearchProductsAsync(TenantId, "COP", null, "cam", 1, 50, Ct);
        var picker = products.LastOrderByName;
        await reader.SearchSellableAsync(TenantId, "COP", null, null, null, "cam", 1, 50, Ct);

        Assert.True(picker);
        Assert.False(products.LastOrderByName);
    }
}

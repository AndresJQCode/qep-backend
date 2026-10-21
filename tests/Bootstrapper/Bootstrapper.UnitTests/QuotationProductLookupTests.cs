using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-20 §3.4 y §4: los adaptadores de Catalog hacia Quotations hablan de **variantes**. La línea pinta el nombre
/// visible, el código, la foto y las escalas de la variante que vende; el precio, las escalas y los empaques salen de la
/// variante y la tasa, del producto.
/// </summary>
public sealed class QuotationProductLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QuotationLookupReturnsVariantNameImageAndScales()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var images = new FakeProductImageLookup(
            Available(world.MainFileId, "https://cdn.example.co/main.png"),
            Available(world.MediumFileId, "https://cdn.example.co/m.png"));
        var lookup = new QuotationProductLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), images);

        var found = await lookup.FindManyAsync(TenantId, [world.Medium.Id.Value], Ct);

        var medium = Assert.Single(found).Value;
        Assert.Equal(world.Medium.Id.Value, medium.VariantId);
        Assert.Equal(world.Product.Id.Value, medium.ProductId);
        Assert.Equal("Camiseta básica – M", medium.Name);
        Assert.Equal("CAM-M", medium.Code);
        Assert.Equal("https://cdn.example.co/m.png", medium.ImageUrl);
        var scale = Assert.Single(medium.Scales);
        Assert.Equal(QuotationPriceScaleRestriction.PackagingUnit, scale.Restriction);
        Assert.Equal(10m, scale.Discount);
        Assert.Equal([12], scale.PackagingUnits);
    }

    // L no eligió foto: muestra la principal del producto. Las dos variantes del lote piden sus fotos en una sola llamada.
    [Fact]
    public async Task QuotationLookupFallsBackToProductMainImage()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var images = new FakeProductImageLookup(
            Available(world.MainFileId, "https://cdn.example.co/main.png"),
            Available(world.MediumFileId, "https://cdn.example.co/m.png"));
        var lookup = new QuotationProductLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), images);

        var found = await lookup.FindManyAsync(TenantId, [world.Medium.Id.Value, world.Large.Id.Value], Ct);

        Assert.Equal("https://cdn.example.co/main.png", found[world.Large.Id.Value].ImageUrl);
        Assert.Equal("https://cdn.example.co/m.png", found[world.Medium.Id.Value].ImageUrl);
        Assert.Equal("Camiseta básica – L", found[world.Large.Id.Value].Name);
        Assert.Empty(found[world.Large.Id.Value].Scales);
        Assert.Equal(1, images.Calls);
    }

    // Una foto propia que no está disponible no cae a la principal: el formulario del producto muestra lo mismo.
    [Fact]
    public async Task QuotationLookupOwnImageNotAvailableGivesNoUrl()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var images = new FakeProductImageLookup(
            Available(world.MainFileId, "https://cdn.example.co/main.png"),
            new ProductImageRef(world.MediumFileId, TenantId, "image/png", false, "https://cdn.example.co/m.png"));
        var lookup = new QuotationProductLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), images);

        var found = await lookup.FindManyAsync(TenantId, [world.Medium.Id.Value], Ct);

        Assert.Null(found[world.Medium.Id.Value].ImageUrl);
    }

    // Una línea vieja sigue necesitando el nombre de lo que vendió aunque el producto se haya dado de baja después.
    [Fact]
    public async Task QuotationLookupIncludesInactiveProducts()
    {
        var world = VariantWorld.Create(TenantId, Now);
        world.Product.Deactivate(Now);
        var lookup = new QuotationProductLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), new FakeProductImageLookup());

        var found = await lookup.FindManyAsync(TenantId, [world.Large.Id.Value], Ct);

        Assert.Equal("Camiseta básica – L", found[world.Large.Id.Value].Name);
    }

    // La variante por defecto se llama como el producto; sin opciones no hay nada que leer de la biblioteca.
    [Fact]
    public async Task QuotationLookupDefaultVariantUsesTheProductNameWithoutReadingOptions()
    {
        var product = SimpleProduct("SH-400");
        var options = new FakeOptionDefinitionRepository();
        var lookup = new QuotationProductLookup(new FakeProductRepository(product), options, new FakeProductImageLookup());

        var found = await lookup.FindManyAsync(TenantId, [product.Id.Value], Ct);

        Assert.Equal("Shampoo 400 ml", found[product.Id.Value].Name);
        Assert.Equal("SH-400", found[product.Id.Value].Code);
        Assert.Equal(0, options.Calls);
    }

    [Fact]
    public async Task QuotationLookupIgnoresVariantsOfAnotherTenant()
    {
        var world = VariantWorld.Create(Guid.CreateVersion7(), Now);
        var lookup = new QuotationProductLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), new FakeProductImageLookup());

        var found = await lookup.FindManyAsync(TenantId, [world.Medium.Id.Value], Ct);

        Assert.Empty(found);
    }

    [Fact]
    public async Task PricingLookupReturnsVariantPricesAndProductTax()
    {
        var rate = TaxRate.Create(TaxRateId.New(), TenantId, "IVA 19", 19, Now);
        var world = VariantWorld.Create(TenantId, Now, taxRateId: rate.Id);
        var lookup = new QuotationProductPricingLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), new FakeTaxRateRepository(rate));

        var large = await lookup.FindVariantAsync(TenantId, world.Large.Id.Value, Ct);

        Assert.NotNull(large);
        Assert.Equal(world.Large.Id.Value, large.VariantId);
        Assert.Equal(world.Product.Id.Value, large.ProductId);
        Assert.Equal(TenantId, large.TenantId);
        Assert.Equal("Camiseta básica – L", large.Name);
        Assert.Equal("CAM-L", large.Code);
        Assert.True(large.IsActive);
        Assert.Equal(new Dictionary<string, decimal> { ["COP"] = 32_000m }, large.Prices);
        Assert.Empty(large.Scales);
        Assert.Equal(19, large.TaxPercentage);
    }

    // Cada variante con sus escalas y sus empaques: los de M no se le prestan a L.
    [Fact]
    public async Task PricingLookupFindManyKeepsEachVariantScales()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var lookup = new QuotationProductPricingLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), new FakeTaxRateRepository());

        var found = await lookup.FindManyAsync(TenantId, [world.Medium.Id.Value, world.Large.Id.Value], Ct);

        Assert.Equal([12], Assert.Single(found[world.Medium.Id.Value].Scales).PackagingUnits);
        Assert.Empty(found[world.Large.Id.Value].Scales);
        Assert.Equal(30_000m, found[world.Medium.Id.Value].Prices["COP"]);
        Assert.Null(found[world.Medium.Id.Value].TaxPercentage);
    }

    // La variante de un producto dado de baja no se vende, pero se devuelve: revalorizar una línea ya cotizada no se bloquea.
    [Fact]
    public async Task PricingLookupVariantOfInactiveProductIsNotSellable()
    {
        var world = VariantWorld.Create(TenantId, Now);
        world.Product.Deactivate(Now);
        var lookup = new QuotationProductPricingLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), new FakeTaxRateRepository());

        var medium = await lookup.FindVariantAsync(TenantId, world.Medium.Id.Value, Ct);

        Assert.NotNull(medium);
        Assert.False(medium.IsActive);
    }

    [Fact]
    public async Task PricingLookupUnknownVariantIsNull()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var lookup = new QuotationProductPricingLookup(
            new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), new FakeTaxRateRepository());

        Assert.Null(await lookup.FindVariantAsync(TenantId, Guid.CreateVersion7(), Ct));
        Assert.Null(await lookup.FindVariantAsync(Guid.CreateVersion7(), world.Medium.Id.Value, Ct));
    }

    private static ProductImageRef Available(Guid fileId, string url) => new(fileId, TenantId, "image/png", true, url);

    private static Product SimpleProduct(string code) =>
        Product.Create(
            ProductId.New(), TenantId, "Shampoo 400 ml",
            ProductDetails.Empty,
            new ProductVariantInput { Code = code, Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 11_900m } } },
            [],
            Now);
}

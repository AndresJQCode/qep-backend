using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// The wire order of a product's price scales.
///
/// The aggregate keeps the scales in the order they were written, and EF materializes them in
/// whatever order the database returns, so neither is a contract the screen can rely on. The
/// grid paints the scales from the smallest tier up, and it should not have to sort them: the
/// Excel export already orders by <c>FromUnit</c> (<c>ClosedXmlProductExportBuilder</c>), and
/// every product response goes through this mapping.
/// </summary>
public sealed class ProductMappingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static PriceScaleInput Scale(int fromUnit, int toUnit) =>
        new(fromUnit, toUnit, 0m, PriceScaleRestriction.Multiple, 1, false);

    [Fact]
    public void OrdersPriceScalesByFromUnit()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            ProductDetails.Empty,
            new ProductVariantInput
            {
                Code = "VS-001",
                Pricing = new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["COP"] = 400_000m, ["USD"] = 100m },
                    Scales = [Scale(20, 29), Scale(1, 9), Scale(10, 19)]
                }
            }, [],
            Now);

        var dto = product.ToDto(ProductReadLookups.Empty(TenantId));

        Assert.Equal([1, 10, 20], Assert.Single(dto.Variants).PriceScales.Select(scale => scale.FromUnit));
    }

    // Spec D10: prices and finals travel in catalogue order (COP, USD, EUR), not insertion order,
    // and the finals are derived for each currency the product has (spec D4).
    [Fact]
    public void PricesAndDerivedFinalsComeInCatalogueOrder()
    {
        var product = Product.Create(
            ProductId.New(), Guid.CreateVersion7(), "Vela de soja", ProductDetails.Empty, new ProductVariantInput
            {
                Code = "VS-001",
                Pricing = new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["EUR"] = 11.4m, ["COP"] = 45_000m },
                    Scales = [new PriceScaleInput(1, 9, 10m, PriceScaleRestriction.Multiple, 1)]
                }
            }, [],
            DateTimeOffset.UtcNow);

        var variant = Assert.Single(product.ToDto(ProductReadLookups.Empty(TenantId)).Variants);

        Assert.Equal(["COP", "EUR"], variant.Prices.Keys);
        var scale = Assert.Single(variant.PriceScales);
        Assert.Equal(new Dictionary<string, decimal> { ["COP"] = 40_500m, ["EUR"] = 10.26m }, scale.Finals);
        Assert.Equal(["COP", "EUR"], scale.Finals.Keys);
    }

    // The finals follow the base prices of the product they are read from: the same discount on
    // a product that only has COP yields a COP final and nothing else.
    [Fact]
    public void FinalsFollowTheBasePricesOfTheProductTheScaleBelongsTo()
    {
        PriceScaleInput scale = new(1, 9, 10m, PriceScaleRestriction.Multiple, 1);
        var both = Product.Create(
            ProductId.New(), TenantId, "Vela", ProductDetails.Empty, new ProductVariantInput
            {
                Code = "VS-BOTH",
                Pricing = new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["COP"] = 45_000m, ["USD"] = 12.5m },
                    Scales = [scale]
                }
            }, [],
            Now);
        var copOnly = Product.Create(
            ProductId.New(), TenantId, "Vela", ProductDetails.Empty, new ProductVariantInput
            {
                Code = "VS-COP",
                Pricing = new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["COP"] = 20_000m },
                    Scales = [scale]
                }
            }, [],
            Now);

        Assert.Equal(
            new Dictionary<string, decimal> { ["COP"] = 40_500m, ["USD"] = 11.25m },
            Assert.Single(Assert.Single(both.ToDto(ProductReadLookups.Empty(TenantId)).Variants).PriceScales).Finals);
        Assert.Equal(
            new Dictionary<string, decimal> { ["COP"] = 18_000m },
            Assert.Single(Assert.Single(copOnly.ToDto(ProductReadLookups.Empty(TenantId)).Variants).PriceScales).Finals);
    }
}

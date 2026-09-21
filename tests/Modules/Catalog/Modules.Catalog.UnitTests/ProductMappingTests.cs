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
        new(fromUnit, toUnit, 0m, PriceScaleRestriction.Multiple, 1, null, 100m, null, false);

    [Fact]
    public void OrdersPriceScalesByFromUnit()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            "VS-001",
            ProductDetails.Empty,
            new ProductPricing
            {
                BaseUsd = 100m,
                Scales = [Scale(20, 29), Scale(1, 9), Scale(10, 19)]
            },
            Now);

        var dto = product.ToDto();

        Assert.Equal([1, 10, 20], dto.PriceScales.Select(scale => scale.FromUnit));
    }
}

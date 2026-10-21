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

    private static SellableVariantSummary Sellable(
        Guid? galleryFileId = null,
        IReadOnlyList<Guid>? valueIds = null) =>
        new(
            new ProductId(Guid.CreateVersion7()),
            new VariantId(Guid.CreateVersion7()),
            "CAM-M",
            "7700000000011",
            0,
            valueIds ?? [],
            // USD primero a propósito: el orden de catálogo es COP, USD.
            new Dictionary<string, decimal> { ["USD"] = 100m, ["COP"] = 400_000m },
            [
                new SellableVariantScale(Guid.CreateVersion7(), 10, 19, 20m, PriceScaleRestriction.PackagingUnit, null, false),
                new SellableVariantScale(Guid.CreateVersion7(), 1, 9, 10m, PriceScaleRestriction.Multiple, 3, true),
            ],
            [6, 12],
            galleryFileId);

    // Tarea 14b: el selector del listado arma lo mismo que el detalle desde la lectura proyectada:
    // nombre visible con los valores, precios en orden de catálogo, escalas por FromUnit con sus
    // finales y el texto de la restricción.
    [Fact]
    public void SellableVariantMapsNameAndScalesLikeTheDetail()
    {
        var medium = Guid.CreateVersion7();
        var variant = Sellable(valueIds: [medium]);

        var dto = variant.ToSellableDto(
            "Camiseta",
            new Dictionary<Guid, string> { [medium] = "M" },
            mainImageUrl: null,
            ProductReadLookups.Empty(TenantId));

        Assert.Equal(variant.Id.Value, dto.Id);
        Assert.Equal("Camiseta – M", dto.Name);
        Assert.Equal("CAM-M", dto.Code);
        Assert.Equal("7700000000011", dto.Barcode);
        Assert.Equal(["COP", "USD"], dto.Prices.Keys);
        Assert.Equal([6, 12], dto.PackagingUnits);
        Assert.Equal([1, 10], dto.PriceScales.Select(scale => scale.FromUnit));
        var multiple = dto.PriceScales.First();
        Assert.Equal("multiple", multiple.Restriction);
        Assert.Equal(3, multiple.Multiple);
        Assert.True(multiple.AllowGrouping);
        Assert.Equal(new Dictionary<string, decimal> { ["COP"] = 360_000m, ["USD"] = 90m }, multiple.Finals);
        Assert.Equal("packaging_unit", dto.PriceScales.Last().Restriction);
        Assert.Null(dto.PriceScales.Last().Multiple);
    }

    // Sin foto propia, la principal del producto; con ella, la suya. Una foto propia que no se pudo
    // resolver no cae a la principal (mismo criterio que el detalle).
    [Fact]
    public void SellableVariantImageIsItsOwnPhotoOrTheMainOne()
    {
        var fileId = Guid.CreateVersion7();
        var lookups = ProductReadLookups.Empty(TenantId) with
        {
            Images = new Dictionary<Guid, ProductImageRef>
            {
                [fileId] = new(fileId, TenantId, "image/png", true, "https://cdn.qep.test/own.png")
            }
        };
        var names = new Dictionary<Guid, string>();

        Assert.Equal(
            "https://cdn.qep.test/own.png",
            Sellable(galleryFileId: fileId).ToSellableDto("Camiseta", names, "https://cdn.qep.test/main.png", lookups).ImageUrl);
        Assert.Equal(
            "https://cdn.qep.test/main.png",
            Sellable().ToSellableDto("Camiseta", names, "https://cdn.qep.test/main.png", lookups).ImageUrl);
        Assert.Null(
            Sellable(galleryFileId: Guid.CreateVersion7())
                .ToSellableDto("Camiseta", names, "https://cdn.qep.test/main.png", lookups).ImageUrl);
    }

    // Un valor sin nombre (la opción ya no está en la biblioteca) no rompe el selector: el nombre
    // queda corto, como en el detalle.
    [Fact]
    public void SellableVariantOmitsAValueWithoutName()
    {
        var known = Guid.CreateVersion7();
        var variant = Sellable(valueIds: [known, Guid.CreateVersion7()]);

        var dto = variant.ToSellableDto(
            "Camiseta",
            new Dictionary<Guid, string> { [known] = "M" },
            mainImageUrl: null,
            ProductReadLookups.Empty(TenantId));

        Assert.Equal("Camiseta – M", dto.Name);
    }

    // El listado con variantes topa la página en 50 (peor caso 102.400 variantes); sin ellas, el
    // tope sigue en 200. Lo topado es la página, no el pedido: la respuesta trae el pageSize aplicado.
    [Fact]
    public void PageSizeWithVariantsIsCappedAtFifty()
    {
        Assert.Equal(50, ProductPaging.NormalizePageSize(200, includeVariants: true));
        Assert.Equal(40, ProductPaging.NormalizePageSize(40, includeVariants: true));
        Assert.Equal(50, ProductPaging.NormalizePageSize(0, includeVariants: true));
        Assert.Equal(200, ProductPaging.NormalizePageSize(500));
        Assert.Equal(200, ProductPaging.NormalizePageSize(200));
    }
}

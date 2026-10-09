using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Copying price scales between products, domain side. Since spec D4 the finals are derived, so
/// the copy carries ranges and discounts only and never touches the target's prices.
/// </summary>
public sealed class PriceScaleCopyTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static Product ProductWith(ProductPricing pricing, string code = "VS-001") =>
        Product.Create(
            ProductId.New(), TenantId, "Vela de soja", code, ProductDetails.Empty, pricing, Now);

    private static Product ProductWithPrices(
        Dictionary<string, decimal> prices, params PriceScaleInput[] scales) =>
        ProductWith(new ProductPricing { Prices = prices, Scales = scales });

    private static PriceScaleInput MultipleScale(
        int fromUnit = 1, int toUnit = 9, decimal discount = 10m,
        int multiple = 3, bool allowGrouping = false) =>
        new(fromUnit, toUnit, discount, PriceScaleRestriction.Multiple, multiple, allowGrouping);

    [Fact]
    public void TheCopyKeepsTheTargetPricesAndCopiesOnlyRangesAndDiscounts()
    {
        var source = ProductWithPrices(new() { ["COP"] = 100_000m }, new PriceScaleInput(1, 9, 10m, PriceScaleRestriction.Multiple, 3));
        var target = ProductWithPrices(new() { ["USD"] = 25m, ["EUR"] = 23m });

        var pricing = PriceScaleCopy.ToPricingFor(source, target);

        Assert.Equal(new Dictionary<string, decimal> { ["USD"] = 25m, ["EUR"] = 23m }, pricing.Prices);
        var scale = Assert.Single(pricing.Scales);
        Assert.Equal((1, 9, 10m, (PriceScaleRestriction?)null, (int?)null, false),
            (scale.FromUnit, scale.ToUnit, scale.Discount, scale.Restriction, scale.Multiple, scale.AllowGrouping));
    }

    // El precio base del destino no es cosa de esta operación: se copian las escalas, no el precio.
    [Fact]
    public void LeavesTheTargetBasePricesAlone()
    {
        var source = ProductWith(new ProductPricing
        {
            Prices = new Dictionary<string, decimal> { ["USD"] = 100m },
            Scales = [MultipleScale()]
        });
        var target = ProductWith(
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 50m, ["COP"] = 200000m } }, "VS-002");

        var pricing = PriceScaleCopy.ToPricingFor(source, target);
        target.ApplyCopiedPriceScales(pricing.Scales, Now.AddMinutes(5));

        Assert.Equal(50m, target.PriceIn("USD"));
        Assert.Equal(200000m, target.PriceIn("COP"));
        Assert.Equal(50m, pricing.Prices["USD"]);
        Assert.Equal(200000m, pricing.Prices["COP"]);
    }

    // Se copia el rango y el descuento, nada más. La restricción y su valor —múltiplo o
    // empaque— y la agrupación dependen del producto, no del tramo: arrastrarlos dejaba a un
    // producto vendiéndose de a 12 porque otro se empaca así. La escala queda incompleta hasta
    // que alguien abra el destino y la termine de configurar.
    [Fact]
    public void CopiesOnlyTheRangeAndTheDiscount()
    {
        var source = ProductWith(new ProductPricing
        {
            Prices = new Dictionary<string, decimal> { ["USD"] = 100m },
            PackagingUnits = [12],
            Scales =
            [
                MultipleScale(fromUnit: 10, toUnit: 20, discount: 20m, multiple: 5, allowGrouping: true),
                new PriceScaleInput(1, 9, 10m, PriceScaleRestriction.PackagingUnit, null)
            ]
        });
        var target = ProductWith(
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 100m }, PackagingUnits = [100, 150] }, "VS-002");

        var pricing = PriceScaleCopy.ToPricingFor(source, target);
        target.ApplyCopiedPriceScales(pricing.Scales, Now.AddMinutes(5));

        // Los empaques tampoco se heredan: el precio que se compara es el del destino.
        Assert.Equal([100, 150], pricing.PackagingUnits);
        Assert.Equal([100, 150], target.PackagingUnits);

        // Ordenadas por rango, no en el orden en que venían.
        var scales = target.PriceScales.ToArray();
        Assert.Equal(2, scales.Length);

        Assert.Equal(1, scales[0].FromUnit);
        Assert.Equal(9, scales[0].ToUnit);
        Assert.Equal(10m, scales[0].Discount);

        Assert.Equal(10, scales[1].FromUnit);
        Assert.Equal(20, scales[1].ToUnit);
        Assert.Equal(20m, scales[1].Discount);

        Assert.All(scales, scale =>
        {
            Assert.Null(scale.Restriction);
            Assert.Null(scale.Multiple);
            Assert.False(scale.AllowGrouping);
        });
    }

    // La copia es el único camino que produce escalas incompletas, y no se abre para lo que
    // venga completo: una entrada con restricción que llega acá es un error de programación.
    [Fact]
    public void RejectsACopiedScaleThatCarriesARestriction()
    {
        var target = ProductWith(new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 100m } }, "VS-002");

        Assert.Throws<ArgumentException>(() =>
            target.ApplyCopiedPriceScales([MultipleScale()], Now.AddMinutes(5)));
    }

    // Recopiar sobre un destino que ya tiene escalas incompletas: el histórico de precios compara
    // descuentos por rango y no puede depender de que la restricción exista.
    [Fact]
    public void DetectsDiscountChangesOnATargetWithIncompleteScales()
    {
        var firstSource = ProductWith(new ProductPricing
        {
            Prices = new Dictionary<string, decimal> { ["USD"] = 100m },
            Scales = [MultipleScale(discount: 10m)]
        });
        var secondSource = ProductWith(new ProductPricing
        {
            Prices = new Dictionary<string, decimal> { ["USD"] = 100m },
            Scales = [MultipleScale(discount: 20m)]
        }, "VS-003");
        var target = ProductWith(new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 100m } }, "VS-002");
        target.ApplyCopiedPriceScales(
            PriceScaleCopy.ToPricingFor(firstSource, target).Scales, Now.AddMinutes(5));

        var pricing = PriceScaleCopy.ToPricingFor(secondSource, target);
        var changes = ProductPriceChangeDetector.Detect(
            target, pricing, Guid.CreateVersion7(), Now.AddMinutes(10));
        target.ApplyCopiedPriceScales(pricing.Scales, Now.AddMinutes(10));

        var change = Assert.Single(changes);
        Assert.Equal(10m, change.PreviousValue);
        Assert.Equal(20m, change.NewValue);
        Assert.Null(Assert.Single(target.PriceScales).Restriction);
    }

    // Reemplazo, no suma: es lo que la pantalla avisa antes de copiar.
    [Fact]
    public void ReplacesTheScalesTheTargetAlreadyHad()
    {
        var source = ProductWith(new ProductPricing
        {
            Prices = new Dictionary<string, decimal> { ["USD"] = 100m },
            Scales = [MultipleScale(fromUnit: 10, toUnit: 20)]
        });
        var target = ProductWith(
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 100m }, Scales = [MultipleScale(fromUnit: 1, toUnit: 9)] },
            "VS-002");

        target.ApplyCopiedPriceScales(
            PriceScaleCopy.ToPricingFor(source, target).Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.PriceScales);
        Assert.Equal(10, copied.FromUnit);
    }

    // Las escalas copiadas son filas nuevas, igual que en un PUT: el id no viaja desde el origen.
    [Fact]
    public void GivesEveryCopiedScaleItsOwnId()
    {
        var source = ProductWith(new ProductPricing
        {
            Prices = new Dictionary<string, decimal> { ["USD"] = 100m },
            Scales = [MultipleScale()]
        });
        var target = ProductWith(new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 100m } }, "VS-002");
        var sourceScaleId = source.PriceScales.Single().Id;

        target.ApplyCopiedPriceScales(
            PriceScaleCopy.ToPricingFor(source, target).Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.PriceScales);
        Assert.NotEqual(sourceScaleId, copied.Id);
        Assert.Equal(target.Id, copied.ProductId);
        Assert.Equal(TenantId, copied.TenantId);
    }

    [Fact]
    public void StampsTheTargetAsModified()
    {
        var source = ProductWith(new ProductPricing
        {
            Prices = new Dictionary<string, decimal> { ["USD"] = 100m },
            Scales = [MultipleScale()]
        });
        var target = ProductWith(new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 100m } }, "VS-002");
        var version = target.Version;
        var modifiedAt = Now.AddMinutes(5);

        target.ApplyCopiedPriceScales(
            PriceScaleCopy.ToPricingFor(source, target).Scales, modifiedAt);

        Assert.Equal(version + 1, target.Version);
        Assert.Equal(modifiedAt, target.UpdatedAt);
    }

    // Misma guarda que Update: un producto inactivo no se edita, y copiarle escalas es editarlo.
    [Fact]
    public void RejectsAnInactiveTarget()
    {
        var source = ProductWith(new ProductPricing
        {
            Prices = new Dictionary<string, decimal> { ["USD"] = 100m },
            Scales = [MultipleScale()]
        });
        var target = ProductWith(new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 100m } }, "VS-002");
        target.Deactivate(Now.AddMinutes(1));

        var error = Assert.Throws<CatalogDomainException>(() =>
            target.ApplyCopiedPriceScales(
                PriceScaleCopy.ToPricingFor(source, target).Scales, Now.AddMinutes(5)));

        Assert.Equal("catalog.product.inactive", error.Code);
    }
}

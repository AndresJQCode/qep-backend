using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Copying price scales between variants, domain side (spec 2026-10-20 §4). Since spec D4 the finals
/// are derived, so the copy carries ranges and discounts only and never touches the target's prices.
/// </summary>
public sealed class PriceScaleCopyTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static Product ProductWith(ProductPricing pricing, string code = "VS-001") =>
        Product.Create(
            ProductId.New(), TenantId, "Vela de soja", ProductDetails.Empty, new ProductVariantInput { Code = code, Pricing = pricing }, [], Now);

    private static Product ProductWithPrices(
        Dictionary<string, decimal> prices, params PriceScaleInput[] scales) =>
        ProductWith(new ProductPricing { Prices = prices, Scales = scales });

    // La copia es de variante a variante: estos productos de una sola variante son el caso de
    // «un producto sin opciones», que se comporta como antes.
    private static ProductPricing CopyPricing(Product source, Product target) =>
        PriceScaleCopy.ToPricingFor(source.Variants.Single(), target.Variants.Single());

    private static void ApplyCopy(
        Product target, IReadOnlyCollection<PriceScaleInput> scales, DateTimeOffset occurredAt) =>
        target.ApplyCopiedPriceScales(
            new Dictionary<VariantId, IReadOnlyCollection<PriceScaleInput>> { [target.Variants.Single().Id] = scales },
            occurredAt);

    // Camiseta con Talla: S y M son dos variantes de un mismo producto, cada una con sus escalas.
    private static Product SizedProduct(
        string name, string codePrefix, ProductPricing smallPricing, ProductPricing mediumPricing)
    {
        var talla = Guid.CreateVersion7();
        return Product.Create(
            ProductId.New(),
            TenantId,
            name,
            ProductDetails.Empty,
            [talla],
            [
                new ProductVariantInput { Code = $"{codePrefix}-S", OptionValueIds = [Guid.CreateVersion7()], Pricing = smallPricing },
                new ProductVariantInput { Code = $"{codePrefix}-M", OptionValueIds = [Guid.CreateVersion7()], Pricing = mediumPricing },
            ],
            [],
            Now);
    }

    private static ProductPricing UsdPricing(decimal usd, params PriceScaleInput[] scales) =>
        new() { Prices = new Dictionary<string, decimal> { ["USD"] = usd }, Scales = scales };

    private static Dictionary<VariantId, IReadOnlyCollection<PriceScaleInput>> ScalesFor(
        params (ProductVariant Variant, ProductPricing Pricing)[] targets) =>
        targets.ToDictionary(
            target => target.Variant.Id,
            target => (IReadOnlyCollection<PriceScaleInput>)target.Pricing.Scales);

    private static PriceScaleInput MultipleScale(
        int fromUnit = 1, int toUnit = 9, decimal discount = 10m,
        int multiple = 3, bool allowGrouping = false) =>
        new(fromUnit, toUnit, discount, PriceScaleRestriction.Multiple, multiple, allowGrouping);

    [Fact]
    public void TheCopyKeepsTheTargetPricesAndCopiesOnlyRangesAndDiscounts()
    {
        var source = ProductWithPrices(new() { ["COP"] = 100_000m }, new PriceScaleInput(1, 9, 10m, PriceScaleRestriction.Multiple, 3));
        var target = ProductWithPrices(new() { ["USD"] = 25m, ["EUR"] = 23m });

        var pricing = CopyPricing(source, target);

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

        var pricing = CopyPricing(source, target);
        ApplyCopy(target, pricing.Scales, Now.AddMinutes(5));

        Assert.Equal(50m, target.Variants.Single().PriceIn("USD"));
        Assert.Equal(200000m, target.Variants.Single().PriceIn("COP"));
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

        var pricing = CopyPricing(source, target);
        ApplyCopy(target, pricing.Scales, Now.AddMinutes(5));

        // Los empaques tampoco se heredan: el precio que se compara es el del destino.
        Assert.Equal([100, 150], pricing.PackagingUnits);
        Assert.Equal([100, 150], target.Variants.Single().PackagingUnits);

        // Ordenadas por rango, no en el orden en que venían.
        var scales = target.Variants.Single().PriceScales.ToArray();
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
            ApplyCopy(target, [MultipleScale()], Now.AddMinutes(5)));
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
        ApplyCopy(target, 
            CopyPricing(firstSource, target).Scales, Now.AddMinutes(5));

        var pricing = CopyPricing(secondSource, target);
        var changes = ProductPriceChangeDetector.DetectForVariant(
            target, target.Variants.Single(), pricing, Guid.CreateVersion7(), Now.AddMinutes(10));
        ApplyCopy(target, pricing.Scales, Now.AddMinutes(10));

        var change = Assert.Single(changes);
        Assert.Equal(10m, change.PreviousValue);
        Assert.Equal(20m, change.NewValue);
        Assert.Null(Assert.Single(target.Variants.Single().PriceScales).Restriction);
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

        ApplyCopy(target, 
            CopyPricing(source, target).Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.Variants.Single().PriceScales);
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
        var sourceScaleId = source.Variants.Single().PriceScales.Single().Id;

        ApplyCopy(target, 
            CopyPricing(source, target).Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.Variants.Single().PriceScales);
        Assert.NotEqual(sourceScaleId, copied.Id);
        Assert.Equal(target.Variants.Single().Id, copied.VariantId);
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

        ApplyCopy(target, 
            CopyPricing(source, target).Scales, modifiedAt);

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
            ApplyCopy(target, 
                CopyPricing(source, target).Scales, Now.AddMinutes(5)));

        Assert.Equal("catalog.product.inactive", error.Code);
    }

    // Tarea 7. El origen es una variante (S de la camiseta) y los destinos son variantes de dos
    // productos distintos: la hermana M de la misma camiseta y la única variante de una vela. Cada
    // destino termina con las escalas del origen y sus precios propios; el origen no cambia.
    [Fact]
    public void CopyFromVariantToVariantsOfSeveralProducts()
    {
        var shirt = SizedProduct(
            "Camiseta", "CAM",
            UsdPricing(20m, new PriceScaleInput(1, 9, 10m, PriceScaleRestriction.Multiple, 3), new PriceScaleInput(10, 50, 20m, PriceScaleRestriction.Multiple, 5)),
            UsdPricing(30m));
        var candle = ProductWithPrices(new() { ["USD"] = 50m });
        var (small, medium) = (shirt.Variants[0], shirt.Variants[1]);
        var candleVariant = candle.Variants.Single();

        var mediumPricing = PriceScaleCopy.ToPricingFor(small, medium);
        var candlePricing = PriceScaleCopy.ToPricingFor(small, candleVariant);
        shirt.ApplyCopiedPriceScales(ScalesFor((medium, mediumPricing)), Now.AddMinutes(5));
        candle.ApplyCopiedPriceScales(ScalesFor((candleVariant, candlePricing)), Now.AddMinutes(5));

        Assert.Equal([(1, 9, 10m), (10, 50, 20m)], medium.PriceScales.OrderBy(scale => scale.FromUnit).Select(scale => (scale.FromUnit, scale.ToUnit, scale.Discount)));
        Assert.Equal([(1, 9, 10m), (10, 50, 20m)], candleVariant.PriceScales.OrderBy(scale => scale.FromUnit).Select(scale => (scale.FromUnit, scale.ToUnit, scale.Discount)));
        Assert.All(medium.PriceScales.Concat(candleVariant.PriceScales), scale =>
        {
            Assert.Null(scale.Restriction);
            Assert.Null(scale.Multiple);
        });
        // Cada fila nueva cuelga de la variante destino, no de la del origen ni del producto.
        Assert.All(medium.PriceScales, scale => Assert.Equal(medium.Id, scale.VariantId));
        Assert.All(candleVariant.PriceScales, scale => Assert.Equal(candleVariant.Id, scale.VariantId));
        // Los precios base de los destinos no se tocan y el origen conserva sus escalas completas.
        Assert.Equal(30m, medium.PriceIn("USD"));
        Assert.Equal(50m, candleVariant.PriceIn("USD"));
        Assert.Equal(2, small.PriceScales.Count);
        Assert.All(small.PriceScales, scale => Assert.NotNull(scale.Restriction));
    }

    // Dos variantes del mismo producto reciben escalas en una sola llamada: el producto cuenta un
    // solo cambio (una subida de Version), no uno por variante.
    [Fact]
    public void CopyToTwoVariantsOfTheSameProductBumpsTheVersionOnce()
    {
        var source = ProductWith(UsdPricing(100m, MultipleScale()), "VS-001");
        var shirt = SizedProduct("Camiseta", "CAM", UsdPricing(20m), UsdPricing(30m));
        var version = shirt.Version;

        shirt.ApplyCopiedPriceScales(
            ScalesFor(
                (shirt.Variants[0], PriceScaleCopy.ToPricingFor(source.Variants.Single(), shirt.Variants[0])),
                (shirt.Variants[1], PriceScaleCopy.ToPricingFor(source.Variants.Single(), shirt.Variants[1]))),
            Now.AddMinutes(5));

        Assert.Equal(version + 1, shirt.Version);
        Assert.All(shirt.Variants, variant => Assert.Single(variant.PriceScales));
    }

    // Origen y destino hermanos: el origen se lee antes de escribir, así que copiar S a M sobre el
    // mismo producto deja las escalas de S intactas.
    [Fact]
    public void CopyBetweenSiblingVariantsLeavesTheSourceAlone()
    {
        var shirt = SizedProduct(
            "Camiseta", "CAM", UsdPricing(20m, MultipleScale(discount: 15m)), UsdPricing(30m, MultipleScale(fromUnit: 100, toUnit: 200)));
        var (small, medium) = (shirt.Variants[0], shirt.Variants[1]);

        shirt.ApplyCopiedPriceScales(
            ScalesFor((medium, PriceScaleCopy.ToPricingFor(small, medium))), Now.AddMinutes(5));

        var copied = Assert.Single(medium.PriceScales);
        Assert.Equal((1, 9, 15m), (copied.FromUnit, copied.ToUnit, copied.Discount));
        var kept = Assert.Single(small.PriceScales);
        Assert.Equal(15m, kept.Discount);
        Assert.NotNull(kept.Restriction);
    }

    // El histórico de una copia a varias variantes deja una fila por variante que cambió, con el
    // VariantId de cada una; la que ya tenía las escalas del origen no deja ninguna.
    [Fact]
    public void CopyRecordsHistoryPerTargetVariantAndSkipsTheOnesThatDidNotChange()
    {
        var source = ProductWith(UsdPricing(100m, MultipleScale(discount: 10m)), "VS-001");
        var shirt = SizedProduct(
            "Camiseta", "CAM", UsdPricing(20m, MultipleScale(discount: 10m)), UsdPricing(30m, MultipleScale(discount: 5m)));
        var (small, medium) = (shirt.Variants[0], shirt.Variants[1]);
        var changedBy = Guid.CreateVersion7();

        var changes = new[] { small, medium }
            .SelectMany(variant => ProductPriceChangeDetector.DetectForVariant(
                shirt, variant, PriceScaleCopy.ToPricingFor(source.Variants.Single(), variant), changedBy, Now))
            .ToArray();

        var change = Assert.Single(changes);
        Assert.Equal(medium.Id, change.VariantId);
        Assert.Equal(ProductPriceField.ScaleDiscount, change.Field);
        Assert.Equal(((decimal?)5m, (decimal?)10m), (change.PreviousValue, change.NewValue));
    }

    // Una variante que no es del producto no se copia en silencio sobre otra: el agregado la rechaza
    // con el mismo código que el PUT, y no deja ninguna de las demás a medias.
    [Fact]
    public void RejectsAVariantThatBelongsToAnotherProduct()
    {
        var source = ProductWith(UsdPricing(100m, MultipleScale()), "VS-001");
        var target = ProductWith(UsdPricing(100m), "VS-002");
        var stranger = ProductWith(UsdPricing(100m), "VS-003").Variants.Single();
        var pricing = PriceScaleCopy.ToPricingFor(source.Variants.Single(), target.Variants.Single());

        var error = Assert.Throws<CatalogDomainException>(() => target.ApplyCopiedPriceScales(
            ScalesFor((target.Variants.Single(), pricing), (stranger, pricing)), Now.AddMinutes(5)));

        Assert.Equal("catalog.variant.not_found", error.Code);
        Assert.Empty(target.Variants.Single().PriceScales);
    }
}

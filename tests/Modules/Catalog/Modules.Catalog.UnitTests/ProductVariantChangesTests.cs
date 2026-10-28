using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Cambiar las opciones de un producto que ya existe (spec 2026-10-20 §3.3.1): lo que
/// <see cref="ProductVariantChanges"/> completa del cuerpo del PUT y lo que el agregado termina
/// guardando. Cada prueba recorre el camino del handler —Resolve, detector, Update— sin base.
/// </summary>
public sealed class ProductVariantChangesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ChangedBy = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly OptionDefinition _talla =
        OptionDefinition.Create(TenantId, "Talla", ["S", "M", "L"], isActive: true, Now);

    private readonly OptionDefinition _color =
        OptionDefinition.Create(TenantId, "Color", ["Azul", "Rojo"], isActive: true, Now);

    // Enfoque de revisión 1. «Camiseta» migrada: una variante por defecto con el id del producto,
    // con su código, su precio y una escala. Se le agrega Talla; la variante por defecto viene en el
    // cuerpo con su id y sin valores ni precio, y las otras dos tallas nuevas sin precio.
    [Fact]
    public void AddingOptionToMigratedProductKeepsVariantIdCodePricesAndHistory()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Camiseta",
            ProductDetails.Empty,
            new ProductVariantInput
            {
                Code = "CAM-001",
                Barcode = "7701234567890",
                Pricing = Pricing(25m, packagingUnits: [12], new PriceScaleInput(10, 49, 5m, PriceScaleRestriction.Multiple, 1)),
            },
            [],
            Now);
        var migratedId = product.Variants.Single().Id;
        Assert.Equal(product.Id.Value, migratedId.Value);

        var inputs = Resolve(
            product,
            [_talla.Id.Value],
            new ProductVariantRequest(migratedId.Value, "CAM-001", "7701234567890", null, null, null, null),
            new ProductVariantRequest(null, "CAM-M", null, [ValueOf(_talla, "M")], null, null, null),
            new ProductVariantRequest(null, "CAM-L", null, [ValueOf(_talla, "L")], null, null, null));

        // Historial: la variante que se convierte conserva su precio, así que el PUT no anota
        // ningún cambio; y las nuevas no dejan filas porque nacen con su precio.
        Assert.Empty(ProductPriceChangeDetector.Detect(product, inputs, ChangedBy, Now));

        product.Update("Camiseta", ProductDetails.Empty, [_talla.Id.Value], inputs, [], Now.AddMinutes(1));

        Assert.Equal([_talla.Id.Value], product.OptionIds);
        Assert.Equal(3, product.Variants.Count);

        var converted = product.Variants[0];
        Assert.Equal(migratedId, converted.Id);
        Assert.Equal("CAM-001", converted.Code);
        Assert.Equal("7701234567890", converted.Barcode);
        Assert.Equal([ValueOf(_talla, "S")], converted.OptionValueIds);
        Assert.Equal(25m, converted.PriceIn("USD"));
        Assert.Equal([12], converted.PackagingUnits);
        var scale = Assert.Single(converted.PriceScales);
        Assert.Equal((10, 49, 5m), (scale.FromUnit, scale.ToUnit, scale.Discount));

        // Las nuevas copian precio, escalas y empaques de la que se convirtió; no su código.
        foreach (var added in product.Variants.Skip(1))
        {
            Assert.NotEqual(migratedId, added.Id);
            Assert.Equal(25m, added.PriceIn("USD"));
            Assert.Equal([12], added.PackagingUnits);
            var copied = Assert.Single(added.PriceScales);
            Assert.Equal((10, 49, 5m), (copied.FromUnit, copied.ToUnit, copied.Discount));
            Assert.Null(added.Barcode);
        }

        Assert.Equal(["CAM-M", "CAM-L"], product.Variants.Skip(1).Select(variant => variant.Code));
    }

    // Color a un producto con Talla: las guardadas conservan su id y sus valores y toman el primer
    // valor activo de Color; la combinación nueva copia el precio de la de su misma talla.
    [Fact]
    public void AddingSecondOptionKeepsExistingVariantIds()
    {
        var product = ProductWithTalla(("CAM-S", "S", 20m), ("CAM-M", "M", 30m));
        var small = product.Variants[0].Id;
        var medium = product.Variants[1].Id;

        var inputs = Resolve(
            product,
            [_talla.Id.Value, _color.Id.Value],
            new ProductVariantRequest(small.Value, "CAM-S", null, null, null, null, null),
            new ProductVariantRequest(medium.Value, "CAM-M", null, null, null, null, null),
            new ProductVariantRequest(null, "CAM-M-ROJO", null, [ValueOf(_talla, "M"), ValueOf(_color, "Rojo")], null, null, null));

        Assert.Empty(ProductPriceChangeDetector.Detect(product, inputs, ChangedBy, Now));
        product.Update(
            "Camiseta", ProductDetails.Empty, [_talla.Id.Value, _color.Id.Value], inputs, [], Now.AddMinutes(1));

        Assert.Equal(3, product.Variants.Count);
        Assert.Equal(small, product.Variants[0].Id);
        Assert.Equal([ValueOf(_talla, "S"), ValueOf(_color, "Azul")], product.Variants[0].OptionValueIds);
        Assert.Equal(medium, product.Variants[1].Id);
        Assert.Equal([ValueOf(_talla, "M"), ValueOf(_color, "Azul")], product.Variants[1].OptionValueIds);
        Assert.Equal(30m, product.Variants[2].PriceIn("USD"));
        Assert.All(product.Variants, variant => Assert.True(variant.IsActive));
    }

    // Quitar Color de Talla × Color: S/Azul y S/Rojo quedan iguales («S»), igual M. Sigue activa la
    // primera de cada par y la otra se desactiva; ninguna se borra ni pierde su id.
    [Fact]
    public void RemovingOptionDeactivatesDuplicatedCombinations()
    {
        var product = ProductWithTallaAndColor();
        var ids = product.Variants.Select(variant => variant.Id).ToArray();

        var inputs = Resolve(
            product,
            [_talla.Id.Value],
            product.Variants
                .Select(variant => new ProductVariantRequest(variant.Id.Value, variant.Code, null, null, null, null, null))
                .ToArray());

        product.Update("Camiseta", ProductDetails.Empty, [_talla.Id.Value], inputs, [], Now.AddMinutes(1));

        Assert.Equal(ids, product.Variants.Select(variant => variant.Id));
        Assert.Equal([true, false, true, false], product.Variants.Select(variant => variant.IsActive));
        var small = ValueOf(_talla, "S");
        var medium = ValueOf(_talla, "M");
        Assert.Equal(
            new[] { small, small, medium, medium },
            product.Variants.Select(variant => Assert.Single(variant.OptionValueIds)));

        var active = product.Variants.Where(variant => variant.IsActive).ToArray();
        Assert.Equal(active.Length, active.Select(variant => variant.CombinationKey).Distinct().Count());
    }

    // Quitar todas las opciones: queda una sola activa, sin valores, que pasa a ser la variante por
    // defecto; las demás se desactivan y conservan su código.
    [Fact]
    public void RemovingAllOptionsLeavesOneActiveDefaultVariant()
    {
        var product = ProductWithTalla(("CAM-S", "S", 20m), ("CAM-M", "M", 30m), ("CAM-L", "L", 40m));

        var inputs = Resolve(
            product,
            [],
            product.Variants
                .Select(variant => new ProductVariantRequest(variant.Id.Value, variant.Code, null, null, null, null, null))
                .ToArray());

        product.Update("Camiseta", ProductDetails.Empty, [], inputs, [], Now.AddMinutes(1));

        Assert.Empty(product.OptionIds);
        var active = Assert.Single(product.Variants, variant => variant.IsActive);
        Assert.Equal("CAM-S", active.Code);
        Assert.Empty(active.OptionValueIds);
        Assert.Equal(20m, active.PriceIn("USD"));
        Assert.Equal(["CAM-S", "CAM-M", "CAM-L"], product.Variants.Select(variant => variant.Code));
    }

    // Lo que el cuerpo manda gana: valores, precio y estado explícitos no se completan.
    [Fact]
    public void ExplicitValuesPricingAndStateAreKeptAsSent()
    {
        var product = ProductWithTalla(("CAM-S", "S", 20m), ("CAM-M", "M", 30m));

        var inputs = Resolve(
            product,
            [_talla.Id.Value],
            new ProductVariantRequest(
                product.Variants[0].Id.Value, "CAM-S", null, [ValueOf(_talla, "L")],
                new ProductPricingRequest(new Dictionary<string, decimal> { ["USD"] = 22m }, null, null), null, false),
            new ProductVariantRequest(product.Variants[1].Id.Value, "CAM-M", null, null, null, null, null));

        Assert.Equal([ValueOf(_talla, "L")], inputs[0].OptionValueIds);
        Assert.Equal(22m, inputs[0].Pricing.Prices["USD"]);
        Assert.False(inputs[0].IsActive);
        // Null en una guardada: los suyos.
        Assert.Equal([ValueOf(_talla, "M")], inputs[1].OptionValueIds);
        Assert.Equal(30m, inputs[1].Pricing.Prices["USD"]);
        Assert.True(inputs[1].IsActive);

        // Y el detector anota sólo el precio que cambió, en su variante.
        var change = Assert.Single(ProductPriceChangeDetector.Detect(product, inputs, ChangedBy, Now));
        Assert.Equal(product.Variants[0].Id, change.VariantId);
        Assert.Equal(20m, change.PreviousValue);
        Assert.Equal(22m, change.NewValue);
    }

    // Revisión independiente, 2026-10-21. Una escala copiada sin completar (la deja
    // POST /products/price-scales/copy, sin restricción) no impide guardar con pricing: null, que es
    // «deja precio, escalas y empaques como están»: la escala sigue ahí, incompleta. Es también el
    // camino de una variante en uso pedida en deletedVariantIds, que se desactiva con su precio.
    [Fact]
    public void SavingWithoutPricingKeepsAnIncompleteCopiedScale()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Aceite",
            ProductDetails.Empty,
            new ProductVariantInput { Code = "ACE-1", Pricing = Pricing(25m, packagingUnits: []) },
            [],
            Now);
        var variant = product.Variants.Single().Id;
        product.ApplyCopiedPriceScales(
            new Dictionary<VariantId, IReadOnlyCollection<PriceScaleInput>>
            {
                [variant] = [new PriceScaleInput(10, 49, 5m, null, null)],
            },
            Now);

        var inputs = Resolve(
            product, [], new ProductVariantRequest(variant.Value, "ACE-1", null, null, null, null, IsActive: false));
        product.Update("Aceite", ProductDetails.Empty, [], inputs, [], Now.AddMinutes(1));

        var saved = product.Variants.Single();
        Assert.False(saved.IsActive);
        var scale = Assert.Single(saved.PriceScales);
        Assert.Equal((10, 49, 5m, (PriceScaleRestriction?)null), (scale.FromUnit, scale.ToUnit, scale.Discount, scale.Restriction));
    }

    // La otra mitad: el formulario que manda pricing explícito con una escala sin restricción sigue
    // recibiendo restriction_required. Lo incompleto sólo se conserva, nunca se pide.
    [Fact]
    public void AnExplicitScaleWithoutRestrictionIsStillRejected()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Aceite",
            ProductDetails.Empty,
            new ProductVariantInput { Code = "ACE-1", Pricing = Pricing(25m, packagingUnits: []) },
            [],
            Now);
        var variant = product.Variants.Single().Id;

        var inputs = Resolve(
            product,
            [],
            new ProductVariantRequest(
                variant.Value, "ACE-1", null, null,
                new ProductPricingRequest(
                    new Dictionary<string, decimal> { ["USD"] = 25m },
                    [new PriceScaleRequest(10, 49, 5m, null, null, null)],
                    null),
                null, null));

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update("Aceite", ProductDetails.Empty, [], inputs, [], Now.AddMinutes(1)));
        Assert.Equal("catalog.product.price_scale.restriction_required", error.Code);
    }

    private IReadOnlyList<ProductVariantInput> Resolve(
        Product product,
        IReadOnlyList<Guid> optionIds,
        params ProductVariantRequest[] variants) =>
        ProductVariantChanges.Resolve(
            product,
            optionIds,
            variants,
            new Dictionary<Guid, OptionDefinition>
            {
                [_talla.Id.Value] = _talla,
                [_color.Id.Value] = _color,
            });

    private Product ProductWithTalla(params (string Code, string Talla, decimal Usd)[] variants) =>
        Product.Create(
            ProductId.New(),
            TenantId,
            "Camiseta",
            ProductDetails.Empty,
            [_talla.Id.Value],
            variants
                .Select(variant => new ProductVariantInput
                {
                    Code = variant.Code,
                    OptionValueIds = [ValueOf(_talla, variant.Talla)],
                    Pricing = Pricing(variant.Usd, packagingUnits: []),
                })
                .ToArray(),
            [],
            Now);

    private Product ProductWithTallaAndColor()
    {
        var combinations = new[] { ("S", "Azul"), ("S", "Rojo"), ("M", "Azul"), ("M", "Rojo") };
        return Product.Create(
            ProductId.New(),
            TenantId,
            "Camiseta",
            ProductDetails.Empty,
            [_talla.Id.Value, _color.Id.Value],
            combinations
                .Select(combination => new ProductVariantInput
                {
                    Code = $"CAM-{combination.Item1}-{combination.Item2}",
                    OptionValueIds = [ValueOf(_talla, combination.Item1), ValueOf(_color, combination.Item2)],
                    Pricing = Pricing(20m, packagingUnits: []),
                })
                .ToArray(),
            [],
            Now);
    }

    private static Guid ValueOf(OptionDefinition option, string value) =>
        option.Values.Single(candidate => candidate.Value == value).Id;

    private static ProductPricing Pricing(decimal usd, int[] packagingUnits, params PriceScaleInput[] scales) => new()
    {
        Prices = new Dictionary<string, decimal> { ["USD"] = usd },
        Scales = scales,
        PackagingUnits = packagingUnits,
    };
}

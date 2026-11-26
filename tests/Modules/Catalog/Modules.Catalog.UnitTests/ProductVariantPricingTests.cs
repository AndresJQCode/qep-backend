using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Un producto sin precio de venta (mayorista fase 3, D-16): lo crea Compras al confirmar una orden vinculada y el distribuidor
/// le pone el precio después. Pre-flight X3: tiene que seguir editable por cada camino —el PUT que no manda precio, el que lo
/// manda, agregar o desactivar variantes, la copia de escalas y el historial de precios—; sólo el formulario que manda un
/// precio vacío sigue respondiendo <c>catalog.product.price_required</c>.
/// </summary>
public sealed class ProductVariantPricingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ChangedBy = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly OptionDefinition _talla =
        OptionDefinition.Create(TenantId, "Talla", ["S", "M"], isActive: true, Now);

    private static Product PricelessProduct(string code = "ABC-1") =>
        Product.Create(
            ProductId.New(),
            TenantId,
            "Aceite de oliva",
            ProductDetails.Empty,
            new ProductVariantInput { Code = code, Pricing = new ProductPricing { AllowsNoPrice = true } },
            [],
            Now);

    private static Product PricedProduct(string code = "VS-001") =>
        Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            ProductDetails.Empty,
            new ProductVariantInput
            {
                Code = code,
                Pricing = new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["COP"] = 20_000m },
                    Scales = [new PriceScaleInput(10, 49, 5m, PriceScaleRestriction.Multiple, 1)],
                },
            },
            [],
            Now);

    private IReadOnlyList<ProductVariantInput> Resolve(
        Product product, IReadOnlyList<Guid> optionIds, params ProductVariantRequest[] variants) =>
        ProductVariantChanges.Resolve(
            product, optionIds, variants, new Dictionary<Guid, OptionDefinition> { [_talla.Id.Value] = _talla });

    private static void Save(Product product, IReadOnlyList<Guid> optionIds, IReadOnlyList<ProductVariantInput> inputs)
    {
        // El orden del handler del PUT: el historial antes de aplicar.
        _ = ProductPriceChangeDetector.Detect(product, inputs, ChangedBy, Now.AddMinutes(1));
        product.Update(product.Name, ProductDetails.Empty, optionIds, inputs, [], Now.AddMinutes(1));
    }

    private static void AssertPriceRequired(Action act) =>
        Assert.Equal("catalog.product.price_required", Assert.Throws<CatalogDomainException>(act).Code);

    [Fact]
    public void ASystemProductWithoutPriceIsAccepted()
    {
        var product = PricelessProduct();

        Assert.Empty(product.Variants.Single().Prices);
    }

    [Fact]
    public void TheFormStillRequiresAPrice() =>
        AssertPriceRequired(() => Product.Create(
            ProductId.New(),
            TenantId,
            "Aceite",
            ProductDetails.Empty,
            new ProductVariantInput { Code = "ABC-1", Pricing = new ProductPricing() },
            [],
            Now));

    [Fact]
    public void APricelessProductSavesFromTheFormWithoutPricing()
    {
        var product = PricelessProduct();
        var variant = product.Variants.Single();

        Save(product, [], Resolve(product, [], new ProductVariantRequest(variant.Id.Value, "ABC-1", null, null, null, null, null)));

        Assert.Empty(product.Variants.Single().Prices);
    }

    [Fact]
    public void APricelessProductTakesThePriceTheFormSends()
    {
        var product = PricelessProduct();
        var variant = product.Variants.Single();
        var pricing = new ProductPricingRequest(new Dictionary<string, decimal> { ["COP"] = 15_000m }, null, null);

        var inputs = Resolve(product, [], new ProductVariantRequest(variant.Id.Value, "ABC-1", null, null, pricing, null, null));
        _ = ProductPriceChangeDetector.Detect(product, inputs, ChangedBy, Now.AddMinutes(1));
        product.Update(product.Name, ProductDetails.Empty, [], inputs, [], Now.AddMinutes(1));

        Assert.Equal(15_000m, product.Variants.Single().PriceIn("COP"));
    }

    [Fact]
    public void TheFormThatSendsAnEmptyPriceIsStillPriceRequired()
    {
        var product = PricelessProduct();
        var variant = product.Variants.Single();
        var empty = new ProductPricingRequest(new Dictionary<string, decimal>(), null, null);

        var inputs = Resolve(product, [], new ProductVariantRequest(variant.Id.Value, "ABC-1", null, null, empty, null, null));

        AssertPriceRequired(() => product.Update(product.Name, ProductDetails.Empty, [], inputs, [], Now.AddMinutes(1)));
    }

    [Fact]
    public void AddingVariantsToAPricelessProductCopiesNoPrice()
    {
        var product = PricelessProduct();
        var variant = product.Variants.Single();
        var small = _talla.Values.Single(value => value.Value == "S").Id;
        var medium = _talla.Values.Single(value => value.Value == "M").Id;

        Save(
            product,
            [_talla.Id.Value],
            Resolve(
                product,
                [_talla.Id.Value],
                new ProductVariantRequest(variant.Id.Value, "ABC-1", null, [small], null, null, null),
                new ProductVariantRequest(null, "ABC-1-M", null, [medium], null, null, null)));

        Assert.Equal(2, product.Variants.Count);
        Assert.All(product.Variants, saved => Assert.Empty(saved.Prices));
    }

    [Fact]
    public void DeactivatingAPricelessVariantKeepsWorking()
    {
        var product = PricelessProduct();
        var variant = product.Variants.Single();

        Save(product, [], Resolve(product, [], new ProductVariantRequest(variant.Id.Value, "ABC-1", null, null, null, null, false)));

        Assert.False(product.Variants.Single().IsActive);
    }

    [Fact]
    public void CopyingScalesOntoAPricelessVariantKeepsItWithoutPrice()
    {
        var source = PricedProduct();
        var target = PricelessProduct();
        var targetVariant = target.Variants.Single();

        var pricing = PriceScaleCopy.ToPricingFor(source.Variants.Single(), targetVariant);
        _ = ProductPriceChangeDetector.DetectForVariant(target, targetVariant, pricing, ChangedBy, Now.AddMinutes(1));
        target.ApplyCopiedPriceScales(
            new Dictionary<VariantId, IReadOnlyCollection<PriceScaleInput>> { [targetVariant.Id] = pricing.Scales },
            Now.AddMinutes(1));

        Assert.True(pricing.AllowsNoPrice);
        Assert.Empty(pricing.Prices);
        Assert.Empty(target.Variants.Single().Prices);
        Assert.Single(target.Variants.Single().PriceScales);

        // Y después de la copia, el PUT sin precio sigue guardando: las escalas incompletas se conservan.
        Save(target, [], Resolve(target, [], new ProductVariantRequest(targetVariant.Id.Value, "ABC-1", null, null, null, null, null)));
        Assert.Empty(target.Variants.Single().Prices);
    }

    [Fact]
    public void ACopyOntoAPricedVariantStillCarriesItsPrice()
    {
        var pricing = PriceScaleCopy.ToPricingFor(PricelessProduct().Variants.Single(), PricedProduct().Variants.Single());

        Assert.False(pricing.AllowsNoPrice);
        Assert.Equal(20_000m, pricing.Prices["COP"]);
    }
}

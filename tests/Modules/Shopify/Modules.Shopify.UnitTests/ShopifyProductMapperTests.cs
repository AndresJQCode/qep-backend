using BuildingBlocks.Domain.Pricing;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-17 §5.3, plan D4 y D5: cómo entra un producto de Shopify a Catalog.</summary>
public sealed class ShopifyProductMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ActorId = Guid.CreateVersion7();
    private static readonly Guid Vat19 = Guid.CreateVersion7();
    private static readonly Guid Exempt = Guid.CreateVersion7();

    private static ShopifyPricingContext Pricing(
        string currency = "COP", bool taxesIncluded = true, bool chargesVat = true, Guid? vat19 = null, Guid? exempt = null, bool noRates = false) =>
        new(
            currency,
            taxesIncluded,
            chargesVat,
            new PriceRounding(0, PriceRoundingMode.Nearest),
            noRates ? new ShopifyDefaultTaxRates(null, null) : new ShopifyDefaultTaxRates(vat19 ?? Vat19, exempt ?? Exempt));

    private static ShopifyVariantSnapshot Variant(long id, string? sku = "SKU", bool taxable = true, decimal price = 10_000m, params string[] values) =>
        new(id, sku, null, price, taxable, values);

    private static ShopifyProductSnapshot Product(
        IReadOnlyList<ShopifyProductOption>? options = null,
        ShopifyProductStatus status = ShopifyProductStatus.Active,
        string title = "Camiseta",
        params ShopifyVariantSnapshot[] variants) =>
        new(7001L, title, status, Now, options ?? [], variants, null);

    private static ShopifyMappedProduct Map(ShopifyProductSnapshot product, ShopifyPricingContext? pricing = null) =>
        ShopifyProductMapper.Map(product, pricing ?? Pricing(), TenantId, ActorId, Now);

    [Fact]
    public void ASingleDefaultTitleVariantHasNoOptions()
    {
        var mapped = Map(Product([new ShopifyProductOption("Title", ["Default Title"])], variants: [Variant(1, values: ["Default Title"])]));

        Assert.Empty(mapped.Product.Options);
        Assert.Empty(Assert.Single(mapped.Product.Variants).OptionValues);
    }

    [Fact]
    public void OptionsKeepShopifyOrderAndTrimmedNames()
    {
        var mapped = Map(Product(
            [new ShopifyProductOption(" Talla ", ["S ", " M"]), new ShopifyProductOption("Color", ["Rojo"])],
            title: "  Camiseta básica ",
            variants: [Variant(1, "A", values: ["S ", "Rojo"]), Variant(2, "B", values: [" M", "Rojo"])]));

        Assert.Equal("Camiseta básica", mapped.Product.Name);
        Assert.Equal(["Talla", "Color"], mapped.Product.Options.Select(option => option.Name));
        Assert.Equal(["S", "M"], mapped.Product.Options[0].Values);
        Assert.Equal(["S", "Rojo"], mapped.Product.Variants[0].OptionValues);
        Assert.Equal(["M", "Rojo"], mapped.Product.Variants[1].OptionValues);
        Assert.Equal((TenantId, ActorId, Now, "COP"), (mapped.Product.TenantId, mapped.Product.ActorId, mapped.Product.OccurredAt, mapped.Product.Currency));
    }

    [Fact]
    public void AVariantWithoutSkuGetsShpCodeAndCounts()
    {
        var mapped = Map(Product(variants: [Variant(4455, sku: "  "), Variant(4456, sku: " CAM-1 ")]));

        Assert.Equal(["shp-4455", "CAM-1"], mapped.Product.Variants.Select(variant => variant.Code));
        Assert.Equal(1, mapped.WithoutSku);
        Assert.Equal("shp-4455", ShopifyProductMapper.CodeOf(Variant(4455, sku: null)));
    }

    [Fact]
    public void ABarcodeIsTrimmedOrNull()
    {
        var mapped = Map(Product(variants: [Variant(1) with { Barcode = " 7701 " }, Variant(2, "B") with { Barcode = "  " }]));

        Assert.Equal(["7701", null], mapped.Product.Variants.Select(variant => variant.Barcode));
        Assert.Equal([1L, 2L], mapped.Product.Variants.Select(variant => variant.ShopifyVariantId));
    }

    [Fact]
    public void ATaxableProductTakesThe19Rate()
    {
        var mapped = Map(Product(variants: [Variant(1, taxable: true)]));

        Assert.Equal(Vat19, mapped.Product.TaxRateId);
        Assert.Empty(mapped.IssueCodes);
    }

    [Fact]
    public void ANonTaxableProductTakesExempt()
    {
        var mapped = Map(Product(variants: [Variant(1, taxable: false)]));

        Assert.Equal(Exempt, mapped.Product.TaxRateId);
        Assert.Empty(mapped.IssueCodes);
    }

    [Fact]
    public void MixedTaxableTakes19AndIsReported()
    {
        var mapped = Map(Product(variants: [Variant(1, "A", taxable: true), Variant(2, "B", taxable: false)]));

        Assert.Equal(Vat19, mapped.Product.TaxRateId);
        Assert.Equal([ShopifyProductCodes.MixedTaxable], mapped.IssueCodes);
    }

    // P4: la tasa se asigna por taxable aunque la empresa no cobre IVA.
    [Fact]
    public void ACompanyWithoutVatStillGetsTheRateByTaxable()
    {
        var mapped = Map(Product(variants: [Variant(1, taxable: true)]), Pricing(chargesVat: false));

        Assert.Equal(Vat19, mapped.Product.TaxRateId);
    }

    [Fact]
    public void TaxesExcludedAddsVatForACompanyWithVatInCop()
    {
        var mapped = Map(Product(variants: [Variant(1, price: 10_000m)]), Pricing(taxesIncluded: false));

        Assert.Equal(11_900m, Assert.Single(mapped.Product.Variants).Price);
    }

    [Fact]
    public void TaxesExcludedRoundsWithTheCurrencyRule()
    {
        var mapped = Map(Product(variants: [Variant(1, price: 10_001m)]), Pricing(taxesIncluded: false));

        // 10.001 × 1,19 = 11.901,19 → 11.901 con 0 decimales al más cercano.
        Assert.Equal(11_901m, Assert.Single(mapped.Product.Variants).Price);
    }

    // Revisión (D5): en un update la tasa del producto es la que tiene guardada (es del asesor, S-2), no la que sale de taxable.
    [Theory]
    [InlineData(true, 0, 10_000)]
    [InlineData(false, 19, 11_900)]
    [InlineData(true, 5, 10_500)]
    public void OnUpdateTheVatAddedIsTheSavedRate(bool taxable, int saved, int expected)
    {
        var mapped = ShopifyProductMapper.Map(
            Product(variants: [Variant(1, taxable: taxable, price: 10_000m)]), Pricing(taxesIncluded: false), TenantId, ActorId, Now, saved);

        Assert.Equal(expected, Assert.Single(mapped.Product.Variants).Price);
    }

    [Fact]
    public void TaxesExcludedDoesNotAddVatForAnExemptProduct()
    {
        var mapped = Map(Product(variants: [Variant(1, taxable: false, price: 10_000m)]), Pricing(taxesIncluded: false));

        Assert.Equal(10_000m, Assert.Single(mapped.Product.Variants).Price);
    }

    [Fact]
    public void TaxesExcludedDoesNotAddVatForACompanyWithoutVat()
    {
        var mapped = Map(Product(variants: [Variant(1, price: 10_000m)]), Pricing(taxesIncluded: false, chargesVat: false));

        Assert.Equal(10_000m, Assert.Single(mapped.Product.Variants).Price);
    }

    [Fact]
    public void TaxesExcludedDoesNotAddVatOutsideCop()
    {
        var mapped = Map(Product(variants: [Variant(1, price: 12.5m)]), Pricing(currency: "USD", taxesIncluded: false));

        Assert.Equal(12.5m, Assert.Single(mapped.Product.Variants).Price);
        Assert.Equal("USD", mapped.Product.Currency);
    }

    [Fact]
    public void WithoutRatesTheProductHasNoRateAndIsReported()
    {
        var mapped = Map(Product(variants: [Variant(1, price: 10_000m)]), Pricing(taxesIncluded: false, noRates: true));

        Assert.Null(mapped.Product.TaxRateId);
        Assert.Equal([ShopifyProductCodes.TaxRateMissing], mapped.IssueCodes);
        Assert.Equal(10_000m, Assert.Single(mapped.Product.Variants).Price);
    }

    [Fact]
    public void DraftMapsToInactive()
    {
        Assert.True(Map(Product(status: ShopifyProductStatus.Active, variants: [Variant(1)])).Product.IsActive);
        Assert.False(Map(Product(status: ShopifyProductStatus.Draft, variants: [Variant(1)])).Product.IsActive);
        Assert.False(Map(Product(status: ShopifyProductStatus.Archived, variants: [Variant(1)])).Product.IsActive);
    }
}

using System.Globalization;
using BuildingBlocks.Domain.Pricing;
using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Spec 2026-11-22 (listas de precios por canal): la lista unificada. Los términos de distribuidor (visibilidad, exclusiones,
/// mostrar precios) vienen de la lista mayorista; el ajuste con signo y la regla del precio, de este spec (D-2, D-3).
/// </summary>
public sealed class PriceListTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);
    private static readonly PriceRounding Pesos = new(0, PriceRoundingMode.Nearest);
    private static readonly PriceRounding Cents = new(2, PriceRoundingMode.Nearest);

    [Fact]
    public void CreateTrimsTheNameAndStartsAtVersionOne()
    {
        var category = Guid.CreateVersion7();

        var list = PriceList.Create(Tenant, "  Tienda online  ", 12.5m, PriceListVisibility.Categories, [category], Now);

        Assert.NotEqual(Guid.Empty, list.Id.Value);
        Assert.Equal((Tenant, "Tienda online", 12.5m, PriceListVisibility.Categories, true, 1L, Now, Now),
            (list.TenantId, list.Name, list.AdjustmentPercent, list.Visibility, list.ShowPrices, list.Version, list.CreatedAt,
                list.UpdatedAt));
        Assert.Equal([category], list.CategoryIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRejected(string name)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            PriceList.Create(Tenant, name, 10m, PriceListVisibility.AllActive, [], Now));

        Assert.Equal("catalog.price_list.name_required", error.Code);
    }

    [Fact]
    public void ANameLongerThanTheLimitIsRejected()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            PriceList.Create(Tenant, new string('a', PriceList.NameMaxLength + 1), 10m, PriceListVisibility.AllActive, [], Now));

        Assert.Equal("catalog.price_list.name_too_long", error.Code);
        Assert.Equal(120, PriceList.NameMaxLength);
    }

    [Theory]
    [InlineData("-100.01")]
    [InlineData("1000")]
    [InlineData("10.005")]
    public void AnAdjustmentOutOfRangeOrWithThreeDecimalsIsRejected(string adjustment)
    {
        var error = Assert.Throws<CatalogDomainException>(() => PriceList.Create(
            Tenant, "Lista", decimal.Parse(adjustment, CultureInfo.InvariantCulture), PriceListVisibility.AllActive, [], Now));

        Assert.Equal("catalog.price_list.adjustment_out_of_range", error.Code);
    }

    [Theory]
    [InlineData("-100")]
    [InlineData("0")]
    [InlineData("999.99")]
    [InlineData("-12.50")]
    public void AnAdjustmentInsideTheRangeIsAccepted(string adjustment)
    {
        var value = decimal.Parse(adjustment, CultureInfo.InvariantCulture);

        var list = PriceList.Create(Tenant, "Lista", value, PriceListVisibility.AllActive, [], Now);

        Assert.Equal(value, list.AdjustmentPercent);
    }

    [Fact]
    public void CategoriesAreRequiredWhenTheVisibilityIsCategories()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            PriceList.Create(Tenant, "Lista", 0m, PriceListVisibility.Categories, [], Now));

        Assert.Equal("catalog.price_list.categories_required", error.Code);
    }

    [Fact]
    public void AllActiveIgnoresTheCategories()
    {
        var list = PriceList.Create(Tenant, "Lista", 0m, PriceListVisibility.AllActive, [Guid.CreateVersion7()], Now);

        Assert.Empty(list.CategoryIds);
    }

    [Fact]
    public void UpdateChangesTheTermsAndBumpsTheVersion()
    {
        var list = PriceList.Create(Tenant, "Lista", 0m, PriceListVisibility.AllActive, [], Now);
        var later = Now.AddHours(1);
        var product = Guid.CreateVersion7();

        list.Update("POS", -5m, PriceListVisibility.AllActive, [], later, showPrices: false,
            [new PriceListExclusion(PriceListExclusionKind.Product, product)]);

        Assert.Equal(("POS", -5m, false, 2L, later), (list.Name, list.AdjustmentPercent, list.ShowPrices, list.Version, list.UpdatedAt));
        Assert.Equal([product], list.ExcludedProductIds);
    }

    [Fact]
    public void TouchingTheOverridesBumpsTheVersion()
    {
        var list = PriceList.Create(Tenant, "Lista", 0m, PriceListVisibility.AllActive, [], Now);
        var later = Now.AddMinutes(5);

        list.TouchOverrides(later);

        Assert.Equal((2L, later), (list.Version, list.UpdatedAt));
    }

    [Fact]
    public void TooManyExclusionsAreRejected()
    {
        var exclusions = Enumerable.Range(0, PriceList.MaxHiddenCategories + 1)
            .Select(_ => new PriceListExclusion(PriceListExclusionKind.Category, Guid.CreateVersion7()))
            .ToList();

        var error = Assert.Throws<CatalogDomainException>(() =>
            PriceList.Create(Tenant, "Lista", 0m, PriceListVisibility.AllActive, [], Now, exclusions: exclusions));

        Assert.Equal("catalog.price_list.exclusions_too_many", error.Code);
    }

    // D-2: el precio fijo gana, tal cual y sin redondear.
    [Fact]
    public void TheOverrideWinsAsIs()
    {
        Assert.Equal(1234.56m, PriceList.PriceFor(1000m, 20m, 1234.56m, Pesos));
    }

    // D-2: sin precio fijo, base más ajuste con la regla de la moneda.
    [Theory]
    [InlineData("1000", "20", "1200")]
    [InlineData("1000", "-15", "850")]
    [InlineData("999", "-33.33", "666")]
    [InlineData("1000", "0", "1000")]
    public void WithoutOverrideTheBaseIsAdjustedAndRounded(string basePrice, string adjustment, string expected)
    {
        var price = PriceList.PriceFor(
            decimal.Parse(basePrice, CultureInfo.InvariantCulture),
            decimal.Parse(adjustment, CultureInfo.InvariantCulture),
            overrideAmount: null,
            Pesos);

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), price);
    }

    [Fact]
    public void TheRoundingRuleIsTheCurrencysOne()
    {
        Assert.Equal(11.11m, PriceList.PriceFor(10.10m, 10m, overrideAmount: null, Cents));
    }

    // D-2: un precio fijo sólo reemplaza. Una variante sin precio base en la moneda sigue sin precio en todo canal: así los filtros
    // «tiene precio en la moneda» de la tienda, el POS y el mayorista siguen valiendo.
    [Fact]
    public void WithoutBaseThereIsNoPriceEvenWithAnOverride()
    {
        Assert.Null(PriceList.PriceFor(null, 10m, 500m, Pesos));
        Assert.Null(PriceList.PriceFor(null, 10m, null, Pesos));
    }

    [Fact]
    public void AMinusHundredAdjustmentGivesZero()
    {
        Assert.Equal(0m, PriceList.PriceFor(1000m, -100m, null, Pesos));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10.001")]
    public void AnOverrideAmountMustBePositiveWithTwoDecimals(string amount)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            PriceListOverride.Create(Tenant, PriceListId.New(), new VariantId(Guid.CreateVersion7()), "COP",
                decimal.Parse(amount, CultureInfo.InvariantCulture)));

        Assert.Equal("catalog.price_list.override_amount_invalid", error.Code);
    }

    [Fact]
    public void AnOverrideCanChangeItsAmount()
    {
        var item = PriceListOverride.Create(Tenant, PriceListId.New(), new VariantId(Guid.CreateVersion7()), "COP", 1000m);

        item.ChangeAmount(1500m);

        Assert.Equal(1500m, item.Amount);
    }
}

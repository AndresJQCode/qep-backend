using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>Spec 2026-11-17 (mayorista): la lista de precios. El dominio da el código; el validador de Application, el
/// campo.</summary>
public sealed class PriceListTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateTrimsTheNameAndStartsAtVersionOne()
    {
        var category = Guid.CreateVersion7();

        var list = PriceList.Create(Tenant, "  Distribuidores A  ", 12.5m, PriceListVisibility.Categories, [category], Now);

        Assert.NotEqual(Guid.Empty, list.Id.Value);
        Assert.Equal((Tenant, "Distribuidores A", 12.5m, PriceListVisibility.Categories, 1L, Now, Now),
            (list.TenantId, list.Name, list.DiscountPercent, list.Visibility, list.Version, list.CreatedAt, list.UpdatedAt));
        Assert.Equal([category], list.CategoryIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRejected(string name)
    {
        var error = Assert.Throws<WholesaleDomainException>(() =>
            PriceList.Create(Tenant, name, 10m, PriceListVisibility.AllActive, [], Now));

        Assert.Equal(WholesaleErrorCodes.PriceListNameRequired, error.Code);
    }

    [Fact]
    public void ANameLongerThanTheLimitIsRejected()
    {
        var error = Assert.Throws<WholesaleDomainException>(() =>
            PriceList.Create(Tenant, new string('a', PriceList.NameMaxLength + 1), 10m, PriceListVisibility.AllActive, [], Now));

        Assert.Equal(WholesaleErrorCodes.PriceListNameTooLong, error.Code);
        Assert.Equal(120, PriceList.NameMaxLength);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    [InlineData("10.005")]
    public void DiscountOutsideZeroToHundredOrWithThreeDecimalsIsRejected(string discount)
    {
        var error = Assert.Throws<WholesaleDomainException>(() => PriceList.Create(
            Tenant, "Lista", decimal.Parse(discount, System.Globalization.CultureInfo.InvariantCulture),
            PriceListVisibility.AllActive, [], Now));

        Assert.Equal(WholesaleErrorCodes.PriceListDiscountInvalid, error.Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("33.33")]
    public void DiscountAtTheBoundsOrWithTwoDecimalsIsAccepted(string discount)
    {
        var value = decimal.Parse(discount, System.Globalization.CultureInfo.InvariantCulture);

        var list = PriceList.Create(Tenant, "Lista", value, PriceListVisibility.AllActive, [], Now);

        Assert.Equal(value, list.DiscountPercent);
    }

    [Fact]
    public void CategoriesVisibilityWithoutIdsIsRejected()
    {
        var error = Assert.Throws<WholesaleDomainException>(() =>
            PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.Categories, [], Now));

        Assert.Equal(WholesaleErrorCodes.PriceListCategoriesRequired, error.Code);
    }

    [Fact]
    public void MoreCategoriesThanTheLimitAreRejected()
    {
        var ids = Enumerable.Range(0, PriceList.MaxCategories + 1).Select(_ => Guid.CreateVersion7()).ToArray();

        var error = Assert.Throws<WholesaleDomainException>(() =>
            PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.Categories, ids, Now));

        Assert.Equal(WholesaleErrorCodes.PriceListCategoriesTooMany, error.Code);
        Assert.Equal(200, PriceList.MaxCategories);
    }

    [Fact]
    public void AllActiveDropsTheCategoryIds()
    {
        var list = PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.AllActive, [Guid.CreateVersion7()], Now);

        Assert.Empty(list.CategoryIds);
    }

    [Fact]
    public void RepeatedCategoryIdsAreKeptOnce()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var list = PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.Categories, [second, first, second], Now);

        Assert.Equal([second, first], list.CategoryIds);
    }

    [Fact]
    public void UpdateBumpsTheVersion()
    {
        var list = PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.AllActive, [], Now);
        var later = Now.AddHours(2);
        var category = Guid.CreateVersion7();

        list.Update(" Lista B ", 20m, PriceListVisibility.Categories, [category], later, showPrices: true);

        Assert.Equal(("Lista B", 20m, PriceListVisibility.Categories, 2L, Now, later),
            (list.Name, list.DiscountPercent, list.Visibility, list.Version, list.CreatedAt, list.UpdatedAt));
        Assert.Equal([category], list.CategoryIds);
    }

    [Fact]
    public void ARejectedUpdateChangesNothing()
    {
        var list = PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.AllActive, [], Now);

        Assert.Throws<WholesaleDomainException>(() =>
            list.Update("Lista B", 101m, PriceListVisibility.AllActive, [], Now.AddHours(1), showPrices: true));

        Assert.Equal(("Lista", 10m, 1L, Now), (list.Name, list.DiscountPercent, list.Version, list.UpdatedAt));
    }

    // Spec 2026-11-19 (mostrar precios): por defecto la lista muestra precios.
    [Fact]
    public void CreateShowsPricesByDefault()
    {
        var list = PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.AllActive, [], Now);

        Assert.True(list.ShowPrices);
    }

    [Fact]
    public void CreateCanHidePrices()
    {
        var list = PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.AllActive, [], Now, showPrices: false);

        Assert.False(list.ShowPrices);
    }

    [Fact]
    public void UpdateChangesShowPricesAndBumpsTheVersion()
    {
        var list = PriceList.Create(Tenant, "Lista", 10m, PriceListVisibility.AllActive, [], Now);

        list.Update("Lista", 10m, PriceListVisibility.AllActive, [], Now.AddHours(1), showPrices: false);

        Assert.Equal((false, 2L), (list.ShowPrices, list.Version));
    }}

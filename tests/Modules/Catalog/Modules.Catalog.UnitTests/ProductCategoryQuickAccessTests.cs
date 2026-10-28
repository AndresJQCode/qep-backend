using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// The register's quick access (spec 2026-10-26-categorias-pos-acceso-rapido, D-1/D-4/D-5): an ordered
/// list per tenant kept as contiguous positions 1..N, replaced whole, that never bumps a version.
/// </summary>
public sealed class ProductCategoryQuickAccessTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static ProductCategory Make(string name, ProductCategoryId? parentId = null) =>
        ProductCategory.Create(ProductCategoryId.New(), TenantId, name, parentId, Now);

    [Fact]
    public void ReplaceNumbersInListOrderFromOneAndAnyCategoryCanBePinned()
    {
        var bebidas = Make("Bebidas");
        var gaseosas = Make("Gaseosas", bebidas.Id);
        var calzado = Make("Calzado");
        var all = new List<ProductCategory> { bebidas, gaseosas, calzado };

        ProductCategoryQuickAccess.Replace(all, [gaseosas.Id, calzado.Id]);

        Assert.Equal(1, gaseosas.PosQuickAccessPosition);
        Assert.Equal(2, calzado.PosQuickAccessPosition);
        Assert.Null(bebidas.PosQuickAccessPosition);
    }

    [Fact]
    public void ReplaceUnpinsWhatIsNotInTheNewListAndReorders()
    {
        var a = Make("A");
        var b = Make("B");
        var c = Make("C");
        var all = new List<ProductCategory> { a, b, c };
        ProductCategoryQuickAccess.Replace(all, [a.Id, b.Id, c.Id]);

        ProductCategoryQuickAccess.Replace(all, [c.Id, a.Id]);

        Assert.Equal(1, c.PosQuickAccessPosition);
        Assert.Equal(2, a.PosQuickAccessPosition);
        Assert.Null(b.PosQuickAccessPosition);
    }

    [Fact]
    public void AnEmptyListUnpinsEverything()
    {
        var a = Make("A");
        var b = Make("B");
        var all = new List<ProductCategory> { a, b };
        ProductCategoryQuickAccess.Replace(all, [a.Id, b.Id]);

        ProductCategoryQuickAccess.Replace(all, []);

        Assert.All(all, category => Assert.Null(category.PosQuickAccessPosition));
    }

    // D-5: an open rename or delete must not fail with 412 because someone reordered the chips.
    [Fact]
    public void PinningNeitherBumpsTheVersionNorTouchesUpdatedAt()
    {
        var a = Make("A");

        ProductCategoryQuickAccess.Replace([a], [a.Id]);

        Assert.Equal(1, a.Version);
        Assert.Equal(Now, a.UpdatedAt);
    }

    [Fact]
    public void ADuplicateIsRejectedAndNothingChanges()
    {
        var a = Make("A");
        var b = Make("B");
        var all = new List<ProductCategory> { a, b };
        ProductCategoryQuickAccess.Replace(all, [b.Id]);

        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryQuickAccess.Replace(all, [a.Id, a.Id]));

        Assert.Equal("catalog.category.quick_access_duplicate", error.Code);
        Assert.Null(a.PosQuickAccessPosition);
        Assert.Equal(1, b.PosQuickAccessPosition);
    }

    // A category of another tenant is never in the list the handler loads: same code as unknown.
    [Fact]
    public void AnIdOutsideTheTenantListIsNotFoundAndNothingChanges()
    {
        var a = Make("A");
        var b = Make("B");
        var all = new List<ProductCategory> { a, b };
        ProductCategoryQuickAccess.Replace(all, [b.Id]);

        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryQuickAccess.Replace(all, [a.Id, ProductCategoryId.New()]));

        Assert.Equal("catalog.category.not_found", error.Code);
        Assert.Null(a.PosQuickAccessPosition);
        Assert.Equal(1, b.PosQuickAccessPosition);
    }

    [Fact]
    public void RemoveTakesItOutAndClosesTheGapKeepingTheOrder()
    {
        var a = Make("A");
        var b = Make("B");
        var c = Make("C");
        var d = Make("D");
        var all = new List<ProductCategory> { a, b, c, d };
        ProductCategoryQuickAccess.Replace(all, [c.Id, a.Id, d.Id]);

        ProductCategoryQuickAccess.Remove(all, a.Id);

        Assert.Null(a.PosQuickAccessPosition);
        Assert.Equal(1, c.PosQuickAccessPosition);
        Assert.Equal(2, d.PosQuickAccessPosition);
        Assert.Null(b.PosQuickAccessPosition);
    }

    [Fact]
    public void RemovingANonPinnedCategoryLeavesTheListAsItWas()
    {
        var a = Make("A");
        var b = Make("B");
        var all = new List<ProductCategory> { a, b };
        ProductCategoryQuickAccess.Replace(all, [b.Id]);

        ProductCategoryQuickAccess.Remove(all, a.Id);

        Assert.Equal(1, b.PosQuickAccessPosition);
        Assert.Null(a.PosQuickAccessPosition);
    }
}

using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class ProductCategoryTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateStartsAtVersionOneWithItsParent()
    {
        var parentId = ProductCategoryId.New();

        var category = ProductCategory.Create(ProductCategoryId.New(), TenantId, "Bebidas", parentId, Now);

        Assert.Equal("Bebidas", category.Name);
        Assert.Equal(parentId, category.ParentId);
        Assert.Equal(1, category.Version);
        Assert.Equal(Now, category.CreatedAt);
        Assert.Equal(Now, category.UpdatedAt);
        Assert.Equal(TenantId, category.TenantId);
    }

    [Fact]
    public void CreateTrimsName()
    {
        var category = ProductCategory.Create(ProductCategoryId.New(), TenantId, "  Bebidas  ", null, Now);

        Assert.Equal("Bebidas", category.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankName(string name)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategory.Create(ProductCategoryId.New(), TenantId, name, null, Now));

        Assert.Equal("catalog.category.name_required", error.Code);
    }

    // The limit is measured after trimming, so padding never costs the user characters.
    [Fact]
    public void CreateAcceptsEightyCharactersAndRejectsEightyOne()
    {
        var accepted = ProductCategory.Create(ProductCategoryId.New(), TenantId, new string('a', 80), null, Now);
        var padded = ProductCategory.Create(
            ProductCategoryId.New(), TenantId, " " + new string('a', 80) + " ", null, Now);
        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategory.Create(ProductCategoryId.New(), TenantId, new string('a', 81), null, Now));

        Assert.Equal(80, accepted.Name.Length);
        Assert.Equal(80, padded.Name.Length);
        Assert.Equal("catalog.category.name_too_long", error.Code);
    }

    [Fact]
    public void UpdateRenamesMovesAndBumpsVersionAndUpdatedAt()
    {
        var category = ProductCategory.Create(ProductCategoryId.New(), TenantId, "Bebidas", null, Now);
        var parentId = ProductCategoryId.New();
        var later = Now.AddMinutes(5);

        category.Update("  Gaseosas ", parentId, later);

        Assert.Equal("Gaseosas", category.Name);
        Assert.Equal(parentId, category.ParentId);
        Assert.Equal(2, category.Version);
        Assert.Equal(Now, category.CreatedAt);
        Assert.Equal(later, category.UpdatedAt);
    }

    [Fact]
    public void UpdateToNullParentMakesItARoot()
    {
        var category = ProductCategory.Create(
            ProductCategoryId.New(), TenantId, "Gaseosas", ProductCategoryId.New(), Now);

        category.Update("Gaseosas", null, Now);

        Assert.Null(category.ParentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateRejectsBlankNameAndKeepsTheCategoryUntouched(string name)
    {
        var category = ProductCategory.Create(ProductCategoryId.New(), TenantId, "Bebidas", null, Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            category.Update(name, ProductCategoryId.New(), Now.AddMinutes(1)));

        Assert.Equal("catalog.category.name_required", error.Code);
        Assert.Equal("Bebidas", category.Name);
        Assert.Null(category.ParentId);
        Assert.Equal(1, category.Version);
    }

    [Fact]
    public void UpdateRejectsANameLongerThanEighty()
    {
        var category = ProductCategory.Create(ProductCategoryId.New(), TenantId, "Bebidas", null, Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            category.Update(new string('a', 81), null, Now));

        Assert.Equal("catalog.category.name_too_long", error.Code);
    }

    [Fact]
    public void ANewCategoryIsNotPinned()
    {
        var category = ProductCategory.Create(ProductCategoryId.New(), TenantId, "Bebidas", null, Now);

        Assert.Null(category.PosQuickAccessPosition);
    }
}

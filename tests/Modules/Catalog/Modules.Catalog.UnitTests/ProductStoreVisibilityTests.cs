using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class ProductStoreVisibilityTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly ProductPricing Pricing =
        new() { Prices = new Dictionary<string, decimal> { ["COP"] = 1000m } };

    private static Product NewProduct() =>
        Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            ProductDetails.Empty,
            new ProductVariantInput
            {
                Code = "VS-001",
                Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 1000m } }
            },
            [],
            Now);

    [Fact]
    public void ANewProductIsNotVisibleInTheStore()
    {
        var product = NewProduct();

        Assert.False(product.VisibleInStore);
    }

    [Fact]
    public void SettingTheSameVisibilityDoesNotBumpTheVersion()
    {
        var product = NewProduct();

        var changed = product.SetStoreVisibility(false, Now.AddHours(1));

        Assert.False(changed);
        Assert.Equal(1, product.Version);
        Assert.Equal(Now, product.UpdatedAt);
    }

    [Fact]
    public void ChangingTheVisibilityBumpsTheVersionAndUpdatedAt()
    {
        var product = NewProduct();
        var later = Now.AddHours(1);

        var changed = product.SetStoreVisibility(true, later);

        Assert.True(changed);
        Assert.True(product.VisibleInStore);
        Assert.Equal(2, product.Version);
        Assert.Equal(later, product.UpdatedAt);
    }

    [Fact]
    public void UpdateWithoutVisibilityKeepsIt()
    {
        var product = NewProduct();
        product.SetStoreVisibility(true, Now);

        product.Update(
            "Vela de coco",
            ProductDetails.Empty,
            [],
            [new ProductVariantInput { Id = product.Variants.Single().Id.Value, Code = "VS-001", Pricing = Pricing }],
            [],
            Now.AddHours(1));

        Assert.True(product.VisibleInStore);
    }

    [Fact]
    public void UpdateWithVisibilityAppliesItInTheSameVersionBump()
    {
        var product = NewProduct();

        product.Update(
            "Vela de soja",
            ProductDetails.Empty,
            [],
            [new ProductVariantInput { Id = product.Variants.Single().Id.Value, Code = "VS-001", Pricing = Pricing }],
            [],
            Now.AddHours(1),
            visibleInStore: true);

        Assert.True(product.VisibleInStore);
        Assert.Equal(2, product.Version);
    }

    [Fact]
    public void CreateWithVisibilityStartsVisible()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            ProductDetails.Empty,
            [],
            [new ProductVariantInput
            {
                Code = "VS-001",
                Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 1000m } }
            }],
            [],
            Now,
            visibleInStore: true);

        Assert.True(product.VisibleInStore);
        Assert.Equal(1, product.Version);
    }
}

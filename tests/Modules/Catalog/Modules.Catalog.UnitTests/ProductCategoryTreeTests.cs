using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class ProductCategoryTreeTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static ProductCategory Make(string name, ProductCategoryId? parentId) =>
        ProductCategory.Create(ProductCategoryId.New(), TenantId, name, parentId, Now);

    // N1..Nn, each the child of the previous one.
    private static List<ProductCategory> Chain(int levels)
    {
        var list = new List<ProductCategory>();
        ProductCategoryId? parent = null;
        for (var i = 1; i <= levels; i++)
        {
            var category = Make($"N{i}", parent);
            list.Add(category);
            parent = category.Id;
        }

        return list;
    }

    [Fact]
    public void ARootHasDepthOneAndAChildTwo()
    {
        var chain = Chain(1);

        Assert.Equal(1, ProductCategoryTree.DepthOf(chain, null));
        Assert.Equal(2, ProductCategoryTree.DepthOf(chain, chain[0].Id));
    }

    [Fact]
    public void ADepthTenChainAcceptsNoChild()
    {
        var chain10 = Chain(10);

        ProductCategoryTree.EnsureCanNestUnder(chain10, chain10[8].Id);
        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryTree.EnsureCanNestUnder(chain10, chain10[9].Id));

        Assert.Equal("catalog.category.too_deep", error.Code);
    }

    [Fact]
    public void AParentOutsideTheListIsParentNotFound()
    {
        var chain3 = Chain(3);

        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryTree.EnsureCanNestUnder(chain3, ProductCategoryId.New()));
        ProductCategoryTree.EnsureCanNestUnder(chain3, null);

        Assert.Equal("catalog.category.parent_not_found", error.Code);
    }

    [Fact]
    public void WithDescendantsReturnsTheRootAndEveryLevel()
    {
        var bebidas = Make("Bebidas", null);
        var gaseosas = Make("Gaseosas", bebidas.Id);
        var jugos = Make("Jugos", bebidas.Id);
        var cola = Make("Cola", gaseosas.Id);
        var all = new List<ProductCategory> { bebidas, gaseosas, jugos, cola };

        var fromRoot = ProductCategoryTree.WithDescendants(all, bebidas.Id);
        var fromLeaf = ProductCategoryTree.WithDescendants(all, jugos.Id);

        Assert.Equal(4, fromRoot.Count);
        Assert.Contains(bebidas.Id, fromRoot);
        Assert.Contains(gaseosas.Id, fromRoot);
        Assert.Contains(jugos.Id, fromRoot);
        Assert.Contains(cola.Id, fromRoot);
        Assert.Equal([jugos.Id], fromLeaf);
    }

    [Fact]
    public void WithDescendantsOfAnUnknownRootIsEmpty()
    {
        Assert.Empty(ProductCategoryTree.WithDescendants(Chain(3), ProductCategoryId.New()));
    }
}

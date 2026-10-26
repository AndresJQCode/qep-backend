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

    [Fact]
    public void HeightOfALeafIsZeroAndOfAChainRootIsItsLevelsBelow()
    {
        var chain = Chain(4);

        Assert.Equal(0, ProductCategoryTree.HeightOf(chain, chain[3].Id));
        Assert.Equal(3, ProductCategoryTree.HeightOf(chain, chain[0].Id));
    }

    [Fact]
    public void HeightOfTakesTheDeepestBranch()
    {
        var root = Make("Raíz", null);
        var shallow = Make("Corta", root.Id);
        var deep = Make("Larga", root.Id);
        var deeper = Make("Más larga", deep.Id);

        Assert.Equal(2, ProductCategoryTree.HeightOf([root, shallow, deep, deeper], root.Id));
    }

    [Fact]
    public void MovingUnderAnotherBranchOrToTheRootIsAllowed()
    {
        var bebidas = Make("Bebidas", null);
        var gaseosas = Make("Gaseosas", bebidas.Id);
        var calzado = Make("Calzado", null);
        var all = new List<ProductCategory> { bebidas, gaseosas, calzado };

        ProductCategoryTree.EnsureCanMove(all, gaseosas.Id, calzado.Id);
        ProductCategoryTree.EnsureCanMove(all, gaseosas.Id, null);
        ProductCategoryTree.EnsureCanMove(all, gaseosas.Id, bebidas.Id);
    }

    [Fact]
    public void MovingUnderAParentOutsideTheListIsParentNotFound()
    {
        var chain = Chain(2);

        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryTree.EnsureCanMove(chain, chain[1].Id, ProductCategoryId.New()));

        Assert.Equal("catalog.category.parent_not_found", error.Code);
    }

    [Fact]
    public void MovingUnderItselfIsACycle()
    {
        var chain = Chain(2);

        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryTree.EnsureCanMove(chain, chain[0].Id, chain[0].Id));

        Assert.Equal("catalog.category.cycle", error.Code);
    }

    [Fact]
    public void MovingUnderADescendantIsACycle()
    {
        var chain = Chain(4);

        var child = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryTree.EnsureCanMove(chain, chain[0].Id, chain[1].Id));
        var grandchild = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryTree.EnsureCanMove(chain, chain[1].Id, chain[3].Id));

        Assert.Equal("catalog.category.cycle", child.Code);
        Assert.Equal("catalog.category.cycle", grandchild.Code);
    }

    // The whole subtree moves: its deepest node, not the category itself, has to fit.
    [Fact]
    public void MovingASubtreeChecksItsDeepestNode()
    {
        var host = Chain(7);
        var subtree = new List<ProductCategory>();
        ProductCategoryId? parent = null;
        for (var i = 1; i <= 3; i++)
        {
            var category = Make($"S{i}", parent);
            subtree.Add(category);
            parent = category.Id;
        }

        var all = host.Concat(subtree).ToList();

        // Under N6 the subtree lands at levels 7..9; under N7 at 8..10; under a deeper one it would not fit.
        ProductCategoryTree.EnsureCanMove(all, subtree[0].Id, host[5].Id);
        ProductCategoryTree.EnsureCanMove(all, subtree[0].Id, host[6].Id);

        var longerHost = Chain(8);
        var withLongerHost = longerHost.Concat(subtree).ToList();
        var error = Assert.Throws<CatalogDomainException>(() =>
            ProductCategoryTree.EnsureCanMove(withLongerHost, subtree[0].Id, longerHost[7].Id));

        Assert.Equal("catalog.category.too_deep", error.Code);
    }
}

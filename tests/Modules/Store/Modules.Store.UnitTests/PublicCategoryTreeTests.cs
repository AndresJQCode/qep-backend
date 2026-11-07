using Modules.Store.Application;

namespace Modules.Store.UnitTests;

public sealed class PublicCategoryTreeTests
{
    private static readonly Guid Root = Guid.CreateVersion7();
    private static readonly Guid Child = Guid.CreateVersion7();
    private static readonly Guid Grandchild = Guid.CreateVersion7();
    private static readonly Guid Empty = Guid.CreateVersion7();

    [Fact]
    public void CountsRollUpToAncestorsAndZerosAreDropped()
    {
        IReadOnlyList<(Guid Id, string Name, Guid? ParentId)> all =
        [
            (Root, "Ropa", null),
            (Child, "Camisetas", Root),
            (Grandchild, "Polos", Child),
            (Empty, "Sin productos", Root),
        ];
        IReadOnlyList<StoreCategoryCount> direct =
        [
            new(Root, 1),
            new(Child, 2),
            new(Grandchild, 3),
        ];

        var tree = PublicCategoryTree.Build(direct, all);

        Assert.Equal([Root, Child, Grandchild], tree.Select(node => node.Id));
        Assert.Equal([6, 5, 3], tree.Select(node => node.ProductCount));
        Assert.Equal([null, Root, Child], tree.Select(node => node.ParentId));
    }

    [Fact]
    public void AChildOfADroppedParentBecomesARoot()
    {
        // El padre no está en el árbol del tenant (borrado o ajeno): el hijo no puede apuntar a un nodo que no sale.
        var missingParent = Guid.CreateVersion7();
        IReadOnlyList<(Guid Id, string Name, Guid? ParentId)> all = [(Child, "Camisetas", missingParent)];

        var tree = PublicCategoryTree.Build([new StoreCategoryCount(Child, 2)], all);

        var node = Assert.Single(tree);
        Assert.Equal(Child, node.Id);
        Assert.Null(node.ParentId);
    }

    [Fact]
    public void ACategoryExpandsToItsDescendantsAndAnUnknownOneToNothing()
    {
        IReadOnlyList<(Guid Id, string Name, Guid? ParentId)> all =
        [
            (Root, "Ropa", null),
            (Child, "Camisetas", Root),
            (Grandchild, "Polos", Child),
            (Empty, "Otra", null),
        ];

        Assert.Equal(
            new HashSet<Guid> { Child, Grandchild }, PublicCategoryTree.WithDescendants(all, Child).ToHashSet());
        Assert.Equal(3, PublicCategoryTree.WithDescendants(all, Root).Count);
        Assert.Empty(PublicCategoryTree.WithDescendants(all, Guid.CreateVersion7()));
    }
}

using Modules.Scheduling.Application;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-21 (Agenda), D-7: sin «ver todas las agendas», un miembro sólo ve y administra los recursos vinculados a su membresía.
public sealed class ResourceScopeTests
{
    [Fact]
    public void TheAllScopeIncludesAnyResource()
    {
        Assert.True(ResourceScope.All.IsAll);
        Assert.True(ResourceScope.All.Includes(Guid.CreateVersion7()));
    }

    [Fact]
    public void AnOnlyScopeIncludesJustItsResources()
    {
        var mine = Guid.CreateVersion7();
        var scope = ResourceScope.Only([mine]);

        Assert.False(scope.IsAll);
        Assert.True(scope.Includes(mine));
        Assert.False(scope.Includes(Guid.CreateVersion7()));
    }

    [Fact]
    public void AMemberWithNoLinkedResourceSeesNothing()
    {
        var scope = ResourceScope.Only([]);

        Assert.False(scope.IsAll);
        Assert.False(scope.Includes(Guid.CreateVersion7()));
        Assert.Empty(scope.ResourceIds);
    }

    [Fact]
    public void ReadAllWinsOverTheLinkedResources()
    {
        Assert.True(ResourceScope.For(readAll: true, [Guid.CreateVersion7()]).IsAll);
        Assert.False(ResourceScope.For(readAll: false, [Guid.CreateVersion7()]).IsAll);
    }

    [Fact]
    public void TheIdsOfTheAllScopeAreNotEnumerable()
    {
        // Un alcance «todos» no tiene lista: quien filtre por ids tiene que mirar IsAll primero, o pediría la lista vacía.
        Assert.Throws<InvalidOperationException>(() => ResourceScope.All.ResourceIds);
    }
}

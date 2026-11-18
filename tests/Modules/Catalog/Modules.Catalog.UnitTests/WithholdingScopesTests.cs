using BuildingBlocks.Domain.Catalog;
using BuildingBlocks.Domain.Taxes;

namespace Modules.Catalog.UnitTests;

/// <summary>Spec §4.2 y §4.3: qué líneas cubre el alcance de una retención y cuándo dos alcances se solapan.</summary>
public sealed class WithholdingScopesTests
{
    [Theory]
    [InlineData(WithholdingScope.All, ProductKind.Physical, true)]
    [InlineData(WithholdingScope.All, ProductKind.Service, true)]
    [InlineData(WithholdingScope.Goods, ProductKind.Physical, true)]
    [InlineData(WithholdingScope.Goods, ProductKind.Service, false)]
    [InlineData(WithholdingScope.Services, ProductKind.Physical, false)]
    [InlineData(WithholdingScope.Services, ProductKind.Service, true)]
    public void CoversMatchesKindToScope(WithholdingScope scope, ProductKind kind, bool expected)
    {
        Assert.Equal(expected, WithholdingScopes.Covers(scope, kind));
    }

    [Fact]
    public void GoodsAndServicesDoNotOverlap()
    {
        Assert.False(WithholdingScopes.Overlap(WithholdingScope.Goods, WithholdingScope.Services));
        Assert.False(WithholdingScopes.Overlap(WithholdingScope.Services, WithholdingScope.Goods));
    }

    [Fact]
    public void AllOverlapsEverything()
    {
        foreach (var other in Enum.GetValues<WithholdingScope>())
        {
            Assert.True(WithholdingScopes.Overlap(WithholdingScope.All, other));
            Assert.True(WithholdingScopes.Overlap(other, WithholdingScope.All));
        }
    }

    [Fact]
    public void SameScopeOverlaps()
    {
        Assert.True(WithholdingScopes.Overlap(WithholdingScope.Goods, WithholdingScope.Goods));
        Assert.True(WithholdingScopes.Overlap(WithholdingScope.Services, WithholdingScope.Services));
    }
}

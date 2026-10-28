using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>Spec D4: the final is derived, never stored. FinalFor is the single derivation —
/// the product response, the Excel export and the contract-migration guard all round the same way.</summary>
public sealed class PriceScaleTests
{
    [Theory]
    [InlineData(45000, 10, 40500)]
    [InlineData(9.97, 15, 8.47)]      // 8.4745 → 8.47
    [InlineData(12.5, 10, 11.25)]
    [InlineData(0.05, 50, 0.03)]      // 0.025 → 0.03, away from zero
    public void FinalForRoundsToTwoDecimalsAwayFromZero(decimal productBase, decimal discount, decimal expected) =>
        Assert.Equal(expected, PriceScale.FinalFor(productBase, discount));

    [Fact]
    public void FinalForWithoutABasePriceIsNull() =>
        Assert.Null(PriceScale.FinalFor(null, 10m));
}

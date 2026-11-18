using BuildingBlocks.Domain.Pricing;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>Spec 2026-11-17 (mayorista), D-10: precio de lista = base × (1 − % / 100), redondeado con la regla del mayorista
/// para su moneda.</summary>
public sealed class WholesalePricingTests
{
    [Fact]
    public void ListPriceAppliesTheDiscountAndTheTenantRounding()
    {
        Assert.Equal(87500m, WholesalePricing.ListPrice(100000m, 12.5m, new PriceRounding(0, PriceRoundingMode.Nearest)));
        Assert.Equal(17.99m, WholesalePricing.ListPrice(19.99m, 10m, new PriceRounding(2, PriceRoundingMode.Nearest)));
    }

    [Fact]
    public void ZeroDiscountKeepsTheBasePriceAndHundredGivesZero()
    {
        var rounding = new PriceRounding(0, PriceRoundingMode.Nearest);

        Assert.Equal(54900m, WholesalePricing.ListPrice(54900m, 0m, rounding));
        Assert.Equal(0m, WholesalePricing.ListPrice(54900m, 100m, rounding));
    }

    [Fact]
    public void RoundingUpModeNeverGoesBelowTheExactValue()
    {
        // 1000 × 0.6667 = 666.7: hacia arriba, 667; al más cercano también, pero hacia abajo, 666.
        Assert.Equal(667m, WholesalePricing.ListPrice(1000m, 33.33m, new PriceRounding(0, PriceRoundingMode.Up)));
        Assert.Equal(666m, WholesalePricing.ListPrice(1000m, 33.33m, new PriceRounding(0, PriceRoundingMode.Down)));
    }
}

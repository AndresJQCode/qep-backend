using BuildingBlocks.Domain.Pricing;
using Modules.Tenancy.Application;

namespace Modules.Tenancy.UnitTests;

public sealed class PriceRoundingDefaultsTests
{
    [Fact]
    public void TheDefaultIsTheCatalogueDecimalsNearest()
    {
        Assert.Equal(new PriceRounding(0, PriceRoundingMode.Nearest), PriceRoundingDefaults.For("COP"));
        Assert.Equal(new PriceRounding(2, PriceRoundingMode.Nearest), PriceRoundingDefaults.For("USD"));
    }

    // El puente de Task 1 (LegacyFor) y el catálogo dicen lo mismo hoy; si el catálogo cambia los
    // decimales de una moneda, esta prueba avisa que el puente quedó viejo.
    [Fact]
    public void TheLegacyRuleMatchesTheDefaultForEveryCatalogueCurrency()
    {
        foreach (var currency in Currencies.All)
        {
            Assert.Equal(PriceRoundingDefaults.For(currency.Code), PriceRounding.LegacyFor(currency.Code));
        }
    }
}

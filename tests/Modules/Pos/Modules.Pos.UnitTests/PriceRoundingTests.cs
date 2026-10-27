using System.Globalization;
using BuildingBlocks.Domain.Pricing;

namespace Modules.Pos.UnitTests;

/// <summary>La regla de redondeo de precios (spec 2026-10-27): decimales y sentido.</summary>
public sealed class PriceRoundingTests
{
    [Theory]
    [InlineData("11.2115", 2, PriceRoundingMode.Up, "11.22")]
    [InlineData("11.2195", 2, PriceRoundingMode.Down, "11.21")]
    [InlineData("0.125", 2, PriceRoundingMode.Nearest, "0.13")] // la mitad se aleja de cero
    [InlineData("23334.50", 0, PriceRoundingMode.Nearest, "23335")]
    [InlineData("11.22", 2, PriceRoundingMode.Up, "11.22")] // ya en la grilla: no sube
    public void ApplyRoundsWithTheRule(string value, int decimals, PriceRoundingMode mode, string expected)
    {
        var rounding = new PriceRounding(decimals, mode);

        Assert.Equal(
            decimal.Parse(expected, CultureInfo.InvariantCulture),
            rounding.Apply(decimal.Parse(value, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void LegacyForPesosIsWholeNearest() =>
        Assert.Equal(new PriceRounding(0, PriceRoundingMode.Nearest), PriceRounding.LegacyFor("COP"));

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    public void LegacyForAnyOtherCurrencyIsCentsNearest(string currency) =>
        Assert.Equal(new PriceRounding(2, PriceRoundingMode.Nearest), PriceRounding.LegacyFor(currency));

    [Fact]
    public void NegativeDecimalsAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceRounding(-1, PriceRoundingMode.Up));

    [Fact]
    public void AnUndefinedModeIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceRounding(2, (PriceRoundingMode)99));
}

using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class CurrenciesTests
{
    [Fact]
    public void TheCatalogueListsCopUsdAndEurInThatOrder()
    {
        Assert.Equal(
            new[]
            {
                new CurrencyInfo("COP", "$", 0),
                new CurrencyInfo("USD", "US$", 2),
                new CurrencyInfo("EUR", "€", 2),
            },
            Currencies.All);
    }

    [Theory]
    [InlineData("COP", "COP")]
    [InlineData(" eur ", "EUR")]
    [InlineData("usd", "USD")]
    public void NormalizeTrimsAndUppercases(string input, string expected) =>
        Assert.Equal(expected, Currencies.Normalize(input));

    [Theory]
    [InlineData("XYZ")]
    [InlineData("US")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeRejectsAnythingOutsideTheCatalogue(string? input)
    {
        var exception = Assert.Throws<TenantDomainException>(() => Currencies.Normalize(input));

        Assert.Equal("tenancy.currency.unsupported", exception.Code);
    }

    [Theory]
    [InlineData("eur", true)]
    [InlineData("MXN", false)]
    [InlineData(null, false)]
    public void IsSupportedAnswersWithoutThrowing(string? input, bool expected) =>
        Assert.Equal(expected, Currencies.IsSupported(input));

    [Fact]
    public void GetReturnsSymbolAndDecimals() =>
        Assert.Equal(new CurrencyInfo("USD", "US$", 2), Currencies.Get(" usd"));

    [Fact]
    public void OrderOfFollowsTheCatalogueAndPutsUnknownCodesLast()
    {
        Assert.Equal(0, Currencies.OrderOf("COP"));
        Assert.Equal(2, Currencies.OrderOf("eur"));
        Assert.Equal(int.MaxValue, Currencies.OrderOf("MXN"));
    }
}

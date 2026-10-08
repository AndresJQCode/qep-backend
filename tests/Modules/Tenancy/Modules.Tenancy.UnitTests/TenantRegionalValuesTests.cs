using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantRegionalValuesTests
{
    [Theory]
    [InlineData("COP", "COP")]
    [InlineData(" usd ", "USD")]
    public void NormalizeCurrencyTrimsAndUppercases(string input, string expected) =>
        Assert.Equal(expected, TenantCurrencies.Normalize(input));

    [Theory]
    [InlineData("EUR")]
    [InlineData("")]
    public void NormalizeCurrencyRejectsAnythingOutsideTheTwoSupported(string input)
    {
        var exception = Assert.Throws<TenantDomainException>(() => TenantCurrencies.Normalize(input));
        Assert.Equal("tenancy.settings.default_currency.invalid", exception.Code);
    }

    [Theory]
    [InlineData("1.234,56", "1.234,56")]
    [InlineData(" 1,234.56 ", "1,234.56")]
    public void NormalizeNumberFormatTrims(string input, string expected) =>
        Assert.Equal(expected, TenantNumberFormats.Normalize(input));

    [Theory]
    [InlineData("1 234,56")]
    [InlineData("")]
    public void NormalizeNumberFormatRejectsUnknownPatterns(string input)
    {
        var exception = Assert.Throws<TenantDomainException>(() => TenantNumberFormats.Normalize(input));
        Assert.Equal("tenancy.settings.number_format.invalid", exception.Code);
    }
}

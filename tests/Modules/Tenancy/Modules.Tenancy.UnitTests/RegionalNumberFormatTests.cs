using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class RegionalNumberFormatTests
{
    [Theory]
    [InlineData("1.234,56", ".", ",")]
    [InlineData("1,234.56", ",", ".")]
    [InlineData(null, ".", ",")]
    [InlineData("x", ".", ",")]
    [InlineData(" 1,234.56 ", ",", ".")]
    public void SeparatorsFollowTheTenantFormat(string? format, string thousands, string decimals)
    {
        var separators = RegionalNumberFormat.Separators(format);

        Assert.Equal(thousands, separators.Thousands);
        Assert.Equal(decimals, separators.Decimal);
    }

    [Fact]
    public void ResolveFallsBackToCommaDecimal()
    {
        Assert.Equal(TenantNumberFormats.CommaDecimal, RegionalNumberFormat.Resolve(null));
        Assert.Equal(TenantNumberFormats.CommaDecimal, RegionalNumberFormat.Resolve("x"));
        Assert.Equal(TenantNumberFormats.DotDecimal, RegionalNumberFormat.Resolve("1,234.56"));
    }

    [Fact]
    public void ToNumberFormatInfoFormatsWithTheTenantSeparators()
    {
        var dotDecimal = RegionalNumberFormat.ToNumberFormatInfo("1,234.56");
        var fallback = RegionalNumberFormat.ToNumberFormatInfo(null);

        Assert.Equal("2,450.50", 2450.5m.ToString("N2", dotDecimal));
        Assert.Equal("2.450.000", 2450000m.ToString("N0", fallback));
        Assert.True(dotDecimal.IsReadOnly);
    }
}

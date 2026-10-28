using Modules.Geography.Domain;

namespace Modules.Geography.UnitTests;

public sealed class CityTextTests
{
    [Theory]
    [InlineData("  Santiago   de  Cali ", "SANTIAGO DE CALI")]
    [InlineData("Bogotá", "BOGOTA")]
    [InlineData("medellín", "MEDELLIN")]
    [InlineData("Cali\t\nValle", "CALI VALLE")]
    public void NormalizeStripsAccentsUppercasesAndCollapsesSpaces(string raw, string expected)
    {
        Assert.Equal(expected, CityText.Normalize(raw));
    }

    [Fact]
    public void NormalizeOfOnlyWhitespaceIsEmpty()
    {
        Assert.Equal(string.Empty, CityText.Normalize(" \t\n "));
    }
}

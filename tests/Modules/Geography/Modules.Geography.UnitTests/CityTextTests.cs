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

    [Theory]
    [InlineData("ABC", 5, "ABC")]
    [InlineData("ABCD EF", 5, "ABCD")] // corta y no deja el espacio final
    [InlineData("AB😀", 3, "AB")] // no parte el par sustituto
    public void TruncateCutsWithoutAPartialPairOrATrailingSpace(string value, int max, string expected) =>
        Assert.Equal(expected, CityText.Truncate(value, max));
}

using Modules.Customers.Application;

namespace Modules.Customers.UnitTests;

/// <summary>La regla del número normalizado (spec 2026-10-24 §3.2): la misma que la expresión SQL del índice.</summary>
public sealed class CustomerIdentificationNormalizerTests
{
    [Theory]
    [InlineData("92-1396892", "921396892")]
    [InlineData(" 900.123.456-1 ", "9001234561")]
    [InlineData("ab-1", "AB1")]
    [InlineData("ÑA 12", "A12")]
    [InlineData("---", "")]
    [InlineData(null, "")]
    public void NormalizeKeepsOnlyAsciiLettersAndDigitsInUpperCase(string? input, string expected) =>
        Assert.Equal(expected, CustomerIdentificationNormalizer.Normalize(input));
}

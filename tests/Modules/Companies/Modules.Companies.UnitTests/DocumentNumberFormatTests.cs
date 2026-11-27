using BuildingBlocks.Domain.Identification;

namespace Modules.Companies.UnitTests;

public sealed class DocumentNumberFormatTests
{
    [Fact]
    public void NumericTypesAreNitCcCe() =>
        Assert.Equal(["NIT", "CC", "CE"], DocumentNumberFormat.NumericTypes);

    [Theory]
    [InlineData("NIT", true)]
    [InlineData(" nit ", true)]
    [InlineData("cc", true)]
    [InlineData("CE", true)]
    [InlineData("PASAPORTE", false)]
    [InlineData("NIF", false)]
    [InlineData("NIE", false)]
    [InlineData("TAX_ID", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RequiresDigitsOnlyForTheNumericTypes(string? type, bool expected) =>
        Assert.Equal(expected, DocumentNumberFormat.RequiresDigits(type));

    [Theory]
    [InlineData("900123456")]
    [InlineData("900.123.456-7")]
    [InlineData("12 345 678")]
    [InlineData(" 123 ")]
    public void IsNumericAcceptsDigitsDotsHyphensAndSpaces(string number) =>
        Assert.True(DocumentNumberFormat.IsNumeric(number));

    [Theory]
    [InlineData("ACME SAS")]
    [InlineData("900123456A")]
    [InlineData("---")]
    [InlineData(" . ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12/34")]
    public void IsNumericRejectsLettersSymbolsAndNumbersWithoutDigits(string? number) =>
        Assert.False(DocumentNumberFormat.IsNumeric(number));

    [Fact]
    public void IsNumericWithoutSpacesRejectsSpaces()
    {
        Assert.False(DocumentNumberFormat.IsNumeric("12 345", allowSpaces: false));
        Assert.True(DocumentNumberFormat.IsNumeric("12.345-6", false));
    }

    [Theory]
    [InlineData("NIT", "ACME SAS", false)]
    [InlineData("PASAPORTE", "AB123456", true)]
    [InlineData("NIE", "X1234567L", true)]
    [InlineData("CC", "1.020.304", true)]
    public void IsValidForAppliesTheRuleOnlyToNumericTypes(string type, string number, bool expected) =>
        Assert.Equal(expected, DocumentNumberFormat.IsValidFor(type, number));
}

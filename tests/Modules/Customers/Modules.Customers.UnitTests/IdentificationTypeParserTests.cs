using BuildingBlocks.Domain.Identification;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Customers conoce los siete códigos del catálogo compartido; qué país acepta cuáles lo decide
/// <see cref="DocumentTypesByCountry"/>, no el enum.</summary>
public sealed class IdentificationTypeParserTests
{
    [Fact]
    public void TheSupportedValuesAreTheSharedCatalogue() =>
        Assert.True(IdentificationTypeParser.SupportedWireValues.ToHashSet(StringComparer.Ordinal).SetEquals(DocumentTypesByCountry.All));

    [Theory]
    [InlineData("NIT")]
    [InlineData("CC")]
    [InlineData("CE")]
    [InlineData("PASAPORTE")]
    [InlineData("NIF")]
    [InlineData("NIE")]
    [InlineData("TAX_ID")]
    public void EveryCodeRoundTripsInAnyCase(string code)
    {
        Assert.Equal(code, IdentificationTypeParser.Parse(code).ToWireValue());
        Assert.Equal(code, IdentificationTypeParser.Parse(code.ToLowerInvariant()).ToWireValue());
    }

    [Fact]
    public void TheMemberNamesAreTheStoredValues() =>
        Assert.Equal(["Nit", "Cc", "Ce", "Pasaporte", "Nif", "Nie", "TaxId"], Enum.GetNames<IdentificationType>());

    [Fact]
    public void AnUnknownCodeFailsAndTheMessageListsTheNewCodes()
    {
        var error = Assert.Throws<CustomersDomainException>(() => IdentificationTypeParser.Parse("XX"));
        Assert.Equal("customers.customer.identification_type_invalid", error.Code);
        Assert.Contains("NIF", error.Message);
    }
}

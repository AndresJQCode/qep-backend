using BuildingBlocks.Domain.Identification;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Quotations conoce los siete códigos del catálogo compartido (duplicados de Customers a propósito);
/// qué país acepta cuáles lo decide <see cref="DocumentTypesByCountry"/>, no el enum.</summary>
public sealed class PartyFiscalTypesParserTests
{
    [Fact]
    public void TheSupportedValuesAreTheSharedCatalogue() =>
        Assert.True(PartyFiscalTypesParser.SupportedIdentificationTypes
            .ToHashSet(StringComparer.Ordinal).SetEquals(DocumentTypesByCountry.All));

    [Theory]
    [InlineData("NIT")] [InlineData("CC")] [InlineData("CE")] [InlineData("PASAPORTE")]
    [InlineData("NIF")] [InlineData("NIE")] [InlineData("TAX_ID")]
    public void EveryCodeRoundTripsInAnyCase(string code)
    {
        Assert.Equal(code, PartyFiscalTypesParser.ParseIdentificationType(code)!.Value.ToWireValue());
        Assert.Equal(code, PartyFiscalTypesParser.ParseIdentificationType(code.ToLowerInvariant())!.Value.ToWireValue());
    }

    [Fact]
    public void TheMemberNamesAreTheStoredValues() =>
        Assert.Equal(["Nit", "Cc", "Ce", "Pasaporte", "Nif", "Nie", "TaxId"], Enum.GetNames<PartyIdentificationType>());

    [Fact]
    public void AnUnknownCodeFailsAndTheMessageListsTheNewCodes()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => PartyFiscalTypesParser.ParseIdentificationType("XX"));
        Assert.Equal("quotations.party.identification_type_invalid", error.Code);
        Assert.Contains("NIF", error.Message);
    }
}

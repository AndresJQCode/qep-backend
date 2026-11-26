using BuildingBlocks.Domain.Identification;
using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

public sealed class DocumentTypesByCountryTests
{
    [Fact]
    public void AllHoldsTheSevenCompanyCodes() =>
        Assert.Equal(["NIT", "CC", "CE", "PASAPORTE", "NIF", "NIE", "TAX_ID"], DocumentTypesByCountry.All);

    [Theory]
    [InlineData("CO", new[] { "NIT", "CC", "CE", "PASAPORTE" })]
    [InlineData("co", new[] { "NIT", "CC", "CE", "PASAPORTE" })]
    [InlineData("ES", new[] { "NIF", "NIE", "PASAPORTE" })]
    [InlineData(" es ", new[] { "NIF", "NIE", "PASAPORTE" })]
    [InlineData("US", new[] { "TAX_ID", "PASAPORTE" })]
    [InlineData("MX", new[] { "TAX_ID", "PASAPORTE" })]
    public void AllowedForFollowsTheCompanyTable(string country, string[] expected) =>
        Assert.Equal(expected, DocumentTypesByCountry.AllowedFor(country));

    [Theory]
    [InlineData("XX")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownTypeIsNotKnown(string? type) => Assert.False(DocumentTypesByCountry.IsKnown(type));

    [Fact]
    public void IsKnownTrimsAndIgnoresCase() => Assert.True(DocumentTypesByCountry.IsKnown(" nif "));

    [Theory]
    [InlineData("cc", "CO", true)]
    [InlineData(" nif ", "es", true)]
    [InlineData("CC", "ES", false)]
    [InlineData("TAX_ID", "CO", false)]
    [InlineData(null, "CO", false)]
    public void IsAllowedChecksTheTypeAgainstTheCountry(string? type, string country, bool expected) =>
        Assert.Equal(expected, DocumentTypesByCountry.IsAllowed(type, country));

    [Theory]
    [InlineData("CO")]
    [InlineData("ES")]
    [InlineData("US")]
    [InlineData("co")]
    public void TheCompanyTableMatchesTheSharedOne(string country) =>
        Assert.True(CompanyDocumentTypes.AllowedFor(country).Select(type => type.ToWire()).ToHashSet()
            .SetEquals(DocumentTypesByCountry.AllowedFor(country)));

    [Fact]
    public void TheCompanyCodesAreTheSharedCodes() =>
        Assert.True(Enum.GetValues<CompanyDocumentType>().Select(type => type.ToWire()).ToHashSet()
            .SetEquals(DocumentTypesByCountry.All));
}

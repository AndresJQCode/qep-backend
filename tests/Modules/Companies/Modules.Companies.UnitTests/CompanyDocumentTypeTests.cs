using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

public sealed class CompanyDocumentTypeTests
{
    [Theory]
    [InlineData("NIT", CompanyDocumentType.Nit)]
    [InlineData("CC", CompanyDocumentType.Cc)]
    [InlineData("CE", CompanyDocumentType.Ce)]
    [InlineData("PASAPORTE", CompanyDocumentType.Pasaporte)]
    [InlineData("NIF", CompanyDocumentType.Nif)]
    [InlineData("NIE", CompanyDocumentType.Nie)]
    [InlineData("TAX_ID", CompanyDocumentType.TaxId)]
    public void WireValueRoundTrips(string wire, CompanyDocumentType expected)
    {
        var parsed = CompanyDocumentTypes.Parse(wire);

        Assert.Equal(expected, parsed);
        Assert.Equal(wire, parsed.ToWire());
    }

    [Fact]
    public void ParseIgnoresCase()
    {
        Assert.Equal(CompanyDocumentType.Nif, CompanyDocumentTypes.Parse("nif"));
    }

    [Fact]
    public void TryParseTrimsAndIgnoresCase()
    {
        Assert.True(CompanyDocumentTypes.TryParse(" tax_id ", out var type));
        Assert.Equal(CompanyDocumentType.TaxId, type);
        Assert.False(CompanyDocumentTypes.TryParse(null, out _));
        Assert.False(CompanyDocumentTypes.TryParse("RUT", out _));
    }

    [Fact]
    public void UnknownWireValueFailsInsteadOfDefaulting()
    {
        var ex = Assert.Throws<CompaniesDomainException>(() => CompanyDocumentTypes.Parse("RUT"));

        Assert.Equal("companies.company.document_type_invalid", ex.Code);
    }

    [Fact]
    public void ColombiaAllowsNitCcCePasaporte() =>
        Assert.True(new HashSet<CompanyDocumentType>
            { CompanyDocumentType.Nit, CompanyDocumentType.Cc, CompanyDocumentType.Ce, CompanyDocumentType.Pasaporte }
            .SetEquals(CompanyDocumentTypes.AllowedFor("CO")));

    [Fact]
    public void SpainAllowsNifNiePasaporte() =>
        Assert.True(new HashSet<CompanyDocumentType>
            { CompanyDocumentType.Nif, CompanyDocumentType.Nie, CompanyDocumentType.Pasaporte }
            .SetEquals(CompanyDocumentTypes.AllowedFor("ES")));

    [Fact]
    public void UnitedStatesAllowsTaxIdPasaporte() =>
        Assert.True(new HashSet<CompanyDocumentType>
            { CompanyDocumentType.TaxId, CompanyDocumentType.Pasaporte }
            .SetEquals(CompanyDocumentTypes.AllowedFor("US")));

    [Fact]
    public void AllowedForIsCaseInsensitive() =>
        Assert.True(CompanyDocumentTypes.AllowedFor(" es ").SetEquals(CompanyDocumentTypes.AllowedFor("ES")));
}

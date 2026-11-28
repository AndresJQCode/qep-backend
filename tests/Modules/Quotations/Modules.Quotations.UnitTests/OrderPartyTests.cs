using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Task 1B de la tienda pública: el carril de la ciudad de una parte (Colombia: ids de DIVIPOLA; otro país: texto).
public sealed class OrderPartyTests
{
    private static readonly OrderId OrderId = Domain.OrderId.New();

    private static QuotationPartyDetails Contact() => new()
    {
        Name = "Bodega",
        Phone = "3001234567",
        Email = "bodega@x.co",
        Address = "Cl 1 # 2-3",
    };

    private static OrderParty Party(QuotationPartyDetails details, QuotationPartyRole role = QuotationPartyRole.Shipping) =>
        OrderParty.Create(OrderId, role, details);

    [Fact]
    public void ABillingPartyWithATypeItsCountryDoesNotAllowIsRejected()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => Party(
            Contact() with { Country = "ES", CityName = "Madrid", IdentificationType = PartyIdentificationType.Cc, IdentificationNumber = "1" },
            QuotationPartyRole.Billing));

        Assert.Equal("quotations.party.document_type_not_allowed", error.Code);
        Assert.Contains("Parties.Billing.IdentificationType", error.FieldErrors.Keys);
    }

    [Theory]
    [InlineData(PartyIdentificationType.Nit, "ACME SAS")]
    [InlineData(PartyIdentificationType.Cc, "12A45")]
    [InlineData(PartyIdentificationType.Ce, "abc")]
    public void ABillingPartyWithALetteredNumberForANumericTypeIsRejected(PartyIdentificationType type, string number)
    {
        var error = Assert.Throws<QuotationsDomainException>(() => Party(
            Contact() with { IdentificationType = type, IdentificationNumber = number },
            QuotationPartyRole.Billing));

        Assert.Equal("quotations.party.identification_number_not_numeric", error.Code);
        Assert.Contains("Parties.Billing.IdentificationNumber", error.FieldErrors.Keys);
    }

    [Fact]
    public void ABillingPartyKeepsAPassportNumberWithLetters() =>
        Assert.Equal(
            "AB123",
            Party(
                Contact() with { IdentificationType = PartyIdentificationType.Pasaporte, IdentificationNumber = "AB123" },
                QuotationPartyRole.Billing).IdentificationNumber);

    [Fact]
    public void ABlankCountryIsColombiaForTheDocument()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => Party(
            Contact() with { IdentificationType = PartyIdentificationType.Nif, IdentificationNumber = "1" },
            QuotationPartyRole.Billing));

        Assert.Equal("quotations.party.document_type_not_allowed", error.Code);
    }

    [Fact]
    public void AShippingPartyIsNeverCheckedBecauseItHasNoDocument() =>
        Assert.Null(Party(Contact() with { Country = "ES", CityName = "Madrid", IdentificationType = PartyIdentificationType.Cc }).IdentificationType);

    [Fact]
    public void ABillingPartyKeepsASpanishNif() =>
        Assert.Equal(
            PartyIdentificationType.Nif,
            Party(
                Contact() with { Country = "ES", CityName = "Madrid", IdentificationType = PartyIdentificationType.Nif, IdentificationNumber = "1" },
                QuotationPartyRole.Billing).IdentificationType);

    [Fact]
    public void AColombianPartyIsCompleteOnlyWithItsIds()
    {
        var withoutIds = Party(Contact());
        var withIds = Party(Contact() with { DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7() });

        Assert.False(withoutIds.IsComplete);
        Assert.True(withIds.IsComplete);
        Assert.Null(withIds.Country);
    }

    [Fact]
    public void AForeignPartyIsCompleteWithACityName()
    {
        var party = Party(Contact() with { Country = "ES", CityName = "Madrid" });

        Assert.True(party.IsComplete);
        Assert.Equal(("ES", "Madrid"), (party.Country, party.CityName));
    }

    [Fact]
    public void AForeignPartyWithoutACityNameIsIncomplete() =>
        Assert.False(Party(Contact() with { Country = "ES" }).IsComplete);

    [Fact]
    public void AForeignPartyWithDivipolaIdsIsRejected()
    {
        var exception = Assert.Throws<QuotationsDomainException>(() => Party(
            Contact() with
            {
                Country = "ES",
                CityName = "Madrid",
                DepartmentId = Guid.CreateVersion7(),
                CityId = Guid.CreateVersion7(),
            },
            QuotationPartyRole.Billing));

        Assert.Equal("quotations.party.city_lane_invalid", exception.Code);
        Assert.Contains("Parties.Billing.CityId", exception.FieldErrors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CO")]
    [InlineData("co")]
    public void AColombianPartyWithACityNameIsRejected(string? country)
    {
        var exception = Assert.Throws<QuotationsDomainException>(() => Party(
            Contact() with { Country = country, CityName = "Madrid" }));

        Assert.Equal("quotations.party.city_lane_invalid", exception.Code);
        Assert.Contains("Parties.Shipping.CityName", exception.FieldErrors.Keys);
    }

    [Fact]
    public void ColombiaIsStoredAsNullWhicheverWayItIsWritten()
    {
        var departmentId = Guid.CreateVersion7();
        var cityId = Guid.CreateVersion7();
        var explicitCo = Party(Contact() with { Country = " co ", DepartmentId = departmentId, CityId = cityId });
        var implicitCo = Party(Contact() with { DepartmentId = departmentId, CityId = cityId });

        Assert.Null(explicitCo.Country);
        Assert.Equal(implicitCo.ToDetails(), explicitCo.ToDetails());
    }

    [Fact]
    public void ToDetailsCarriesTheForeignPlaceSoConversionKeepsIt()
    {
        var details = Party(Contact() with { Country = "ES", CityName = "  Madrid " }).ToDetails();

        Assert.Equal(("ES", "Madrid"), (details.Country, details.CityName));
    }

    [Fact]
    public void ToDetailsCarriesRegion()
    {
        var regionId = Guid.CreateVersion7();
        var details = Party(Contact() with { Country = "ES", CityName = "Madrid", RegionId = regionId }).ToDetails();

        Assert.Equal(regionId, details.RegionId);
    }

    [Fact]
    public void EmptyRegionIdIsNoRegion()
    {
        var party = Party(Contact() with { Country = "ES", CityName = "Madrid", RegionId = Guid.Empty });

        Assert.Null(party.RegionId);
    }

    [Theory]
    [InlineData("E")]
    [InlineData("ESP")]
    [InlineData("E1")]
    public void ACountryThatIsNotAlphaTwoIsRejected(string country)
    {
        var exception = Assert.Throws<QuotationsDomainException>(() => Party(
            Contact() with { Country = country, CityName = "Madrid" }));

        Assert.Equal("quotations.party.country_invalid", exception.Code);
    }

    [Fact]
    public void ACityNameLongerThanTheLimitIsRejected() =>
        Assert.Throws<QuotationsDomainException>(() => Party(
            Contact() with { Country = "ES", CityName = new string('x', QuotationPartyDetails.CityNameMaxLength + 1) }));

    [Fact]
    public void AShippingPartyKeepsTheTrimmedPostalCode()
    {
        var party = Party(Contact() with { Country = "ES", CityName = "Madrid", PostalCode = " 28013 " });

        Assert.Equal("28013", party.PostalCode);
        Assert.Equal("28013", party.ToDetails().PostalCode);
    }

    [Fact]
    public void APostalCodeMakesTwoShippingPartiesDifferent() =>
        Assert.NotEqual(
            Party(Contact() with { PostalCode = "050021" }).ToDetails(),
            Party(Contact() with { PostalCode = "050022" }).ToDetails());

    [Fact]
    public void ApplyingDetailsWithoutAPostalCodeClearsIt()
    {
        var party = Party(Contact() with { PostalCode = "050021" });

        party.Apply(Contact());

        Assert.Null(party.PostalCode);
    }

    [Fact]
    public void AShippingPostalCodeLongerThanTheLimitIsRejectedWithItsField()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => Party(
            Contact() with { PostalCode = new string('1', QuotationPartyDetails.PostalCodeMaxLength + 1) }));

        Assert.Equal("quotations.party.postal_code_too_long", error.Code);
        Assert.Contains("Parties.Shipping.PostalCode", error.FieldErrors.Keys);
    }

    [Fact]
    public void ABillingPartyNeverKeepsAPostalCode() =>
        Assert.Null(Party(Contact() with { PostalCode = "050021" }, QuotationPartyRole.Billing).PostalCode);

    // Spec 2026-11-27: la facturación guarda CO literal (null ya es «sin país»); el envío sigue guardándolo como null.
    [Fact]
    public void ABillingPartyStoresColombiaLiteral()
    {
        var billing = Party(
            Contact() with { Country = " co ", DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7() },
            QuotationPartyRole.Billing);

        Assert.Equal("CO", billing.Country);
        Assert.Equal("CO", billing.ToDetails().Country);
    }

    [Fact]
    public void AShippingPartyStillStoresColombiaAsNull() =>
        Assert.Null(Party(Contact() with { Country = "CO", DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7() }).Country);

    [Fact]
    public void ABillingPartyOutsideColombiaKeepsItsCountry() =>
        Assert.Equal("ES", Party(Contact() with { Country = "es", CityName = "Madrid" }, QuotationPartyRole.Billing).EffectiveCountry);

    [Fact]
    public void AnOldBillingRowWithACityReadsAsColombiaAndIsNotPending()
    {
        var billing = Party(
            Contact() with { DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7() }, QuotationPartyRole.Billing);

        Assert.Null(billing.Country);
        Assert.Equal("CO", billing.EffectiveCountry);
        Assert.Equal("CO", billing.ToDetails().Country);
        Assert.False(billing.IsCountryPending);
    }

    [Fact]
    public void ABillingRowWithANameButNoCountryNorCityIsPending()
    {
        var billing = Party(Contact(), QuotationPartyRole.Billing);

        Assert.Null(billing.EffectiveCountry);
        Assert.Null(billing.ToDetails().Country);
        Assert.True(billing.IsCountryPending);
    }

    [Fact]
    public void ABillingRowWithoutANameIsNeverPending() =>
        Assert.False(Party(Contact() with { Name = " " }, QuotationPartyRole.Billing).IsCountryPending);

    [Fact]
    public void AShippingPartyIsNeverPendingAndItsEffectiveCountryIsItsStoredOne()
    {
        var shipping = Party(Contact() with { DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7() });

        Assert.False(shipping.IsCountryPending);
        Assert.Null(shipping.EffectiveCountry);
        Assert.Null(shipping.ToDetails().Country);
    }
}

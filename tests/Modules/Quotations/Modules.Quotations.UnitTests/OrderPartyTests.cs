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
}

using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Task 1B de la tienda pública, del lado de la cotización: QuotationParty sólo se construye desde el agregado.
public sealed class QuotationPartyTests
{
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());

    private static Quotation NewQuotation(QuotationPartyDetails? billing, QuotationPartyDetails? shipping = null) =>
        Quotation.Create(
            QuotationId.New(),
            Guid.CreateVersion7(),
            "QUO-2026-0001",
            Guid.CreateVersion7(),
            AdvisorId,
            new DateOnly(2026, 9, 30),
            paymentMethod: "Transferencia bancaria",
            notes: null,
            new QuotationParties(billing, shipping),
            billingAccount: null,
            "COP",
            customerVatSurplus: false,
            customerWithholdings: [],
            MunicipalContext.None,
            AdvisorId,
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

    private static QuotationPartyDetails Foreign() => new()
    {
        Name = "Bodega Madrid",
        Phone = "+34600111222",
        Email = "bodega@x.es",
        Address = "Calle Mayor 1",
        Country = "ES",
        CityName = "Madrid",
    };

    [Fact]
    public void AColombianPartyIsCompleteOnlyWithItsIds()
    {
        var incomplete = NewQuotation(null, Foreign() with { Country = null, CityName = null });
        var complete = NewQuotation(null, Foreign() with
        {
            Country = null,
            CityName = null,
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
        });

        Assert.False(incomplete.Shipping!.IsComplete);
        Assert.True(complete.Shipping!.IsComplete);
        Assert.Null(complete.Shipping.Country);
    }

    [Fact]
    public void ColombianPartyWithRegionIsLaneInvalid()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewQuotation(null, Foreign() with { Country = null, CityName = null, RegionId = Guid.CreateVersion7() }));

        Assert.Equal("quotations.party.city_lane_invalid", error.Code);
        Assert.Contains("Parties.Shipping.RegionId", error.FieldErrors.Keys);
    }

    [Fact]
    public void ForeignPartyKeepsRegion()
    {
        var regionId = Guid.CreateVersion7();
        var quotation = NewQuotation(null, Foreign() with { RegionId = regionId });

        Assert.Equal(regionId, quotation.Shipping!.RegionId);
        Assert.Equal(regionId, quotation.Shipping.ToDetails().RegionId);
    }

    [Fact]
    public void AForeignPartyIsCompleteWithACityName()
    {
        var quotation = NewQuotation(null, Foreign());

        Assert.True(quotation.Shipping!.IsComplete);
        Assert.Equal(("ES", "Madrid"), (quotation.Shipping.Country, quotation.Shipping.CityName));
    }

    [Fact]
    public void AForeignPartyWithDivipolaIdsIsRejected()
    {
        var exception = Assert.Throws<QuotationsDomainException>(() => NewQuotation(
            null, Foreign() with { CityId = Guid.CreateVersion7() }));

        Assert.Equal("quotations.party.city_lane_invalid", exception.Code);
        Assert.Contains("Parties.Shipping.CityId", exception.FieldErrors.Keys);
    }

    [Fact]
    public void ToDetailsCarriesTheForeignPlaceSoConversionKeepsIt()
    {
        var details = NewQuotation(null, Foreign()).Shipping!.ToDetails();

        Assert.Equal(("ES", "Madrid"), (details.Country, details.CityName));
    }

    // Código postal de la entrega (spec 2026-11-24): recortado, vacío es null, tope de 20 y sólo en la parte de envío.
    [Fact]
    public void TheShippingPostalCodeIsTrimmedAndTravelsWithTheDetails()
    {
        var quotation = NewQuotation(null, Foreign() with { PostalCode = "  28013  " });

        Assert.Equal("28013", quotation.Shipping!.PostalCode);
        Assert.Equal("28013", quotation.Shipping.ToDetails().PostalCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankShippingPostalCodeIsNull(string? postalCode) =>
        Assert.Null(NewQuotation(null, Foreign() with { PostalCode = postalCode }).Shipping!.PostalCode);

    [Fact]
    public void AColombianShippingPartyKeepsAPostalCode()
    {
        var quotation = NewQuotation(null, Foreign() with
        {
            Country = null,
            CityName = null,
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
            PostalCode = "050021",
        });

        Assert.Equal("050021", quotation.Shipping!.PostalCode);
    }

    [Fact]
    public void AShippingPostalCodeOfTwentyCharactersIsKept()
    {
        var postalCode = new string('1', 20);

        Assert.Equal(20, QuotationPartyDetails.PostalCodeMaxLength);
        Assert.Equal(postalCode, NewQuotation(null, Foreign() with { PostalCode = $" {postalCode} " }).Shipping!.PostalCode);
    }

    [Fact]
    public void AShippingPostalCodeOfTwentyOneCharactersIsRejectedWithItsField()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewQuotation(null, Foreign() with { PostalCode = new string('1', 21) }));

        Assert.Equal("quotations.party.postal_code_too_long", error.Code);
        Assert.Contains("Parties.Shipping.PostalCode", error.FieldErrors.Keys);
    }

    // La facturación no lo guarda: se descarta antes de normalizar, así que ni siquiera uno largo es un error.
    [Theory]
    [InlineData("28013")]
    [InlineData("123456789012345678901")]
    public void ABillingPartyDropsThePostalCode(string postalCode)
    {
        var quotation = NewQuotation(Foreign() with
        {
            IdentificationNumber = "X1234567L",
            IdentificationType = PartyIdentificationType.Nie,
            PostalCode = postalCode,
        });

        Assert.Null(quotation.Billing!.PostalCode);
        Assert.Null(quotation.Billing.ToDetails().PostalCode);
    }

    // Spec 2026-11-27: la facturación guarda CO literal; el envío sigue guardándolo como null.
    [Fact]
    public void ABillingPartyStoresColombiaLiteralAndAShippingPartyStoresNull()
    {
        var colombian = Foreign() with
        {
            Country = "co",
            CityName = null,
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
            IdentificationNumber = "900123456",
            IdentificationType = PartyIdentificationType.Nit,
        };

        var quotation = NewQuotation(colombian, colombian);

        Assert.Equal("CO", quotation.Billing!.Country);
        Assert.Null(quotation.Shipping!.Country);
    }

    [Fact]
    public void AnOldBillingRowWithACityReadsAsColombiaAndANamedOneWithoutEitherIsPending()
    {
        var nit = Foreign() with
        {
            CityName = null,
            IdentificationNumber = "900123456",
            IdentificationType = PartyIdentificationType.Nit,
        };
        var old = NewQuotation(nit with
        {
            Country = null,
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
        }).Billing!;
        var pending = NewQuotation(nit with { Country = null }).Billing!;
        var explicitCo = NewQuotation(nit with { Country = "CO" }).Billing!;
        var foreign = NewQuotation(Foreign() with
        {
            IdentificationNumber = "X1234567L",
            IdentificationType = PartyIdentificationType.Nie,
        }).Billing!;

        Assert.Equal(("CO", false), (old.EffectiveCountry, old.IsCountryPending));
        Assert.Equal((null, true), (pending.EffectiveCountry, pending.IsCountryPending));
        Assert.Equal(("CO", false), (explicitCo.EffectiveCountry, explicitCo.IsCountryPending));
        Assert.False(foreign.IsCountryPending);
        Assert.Equal("CO", old.ToDetails().Country);
        Assert.Null(pending.ToDetails().Country);
    }

    // Spec 2026-11-27: una facturación vieja (país null con ciudad) vuelta a guardar con CO no es un cambio, así que el
    // historial no anota «Editó datos de facturación».
    [Fact]
    public void TheHistoryReadsAnOldColombianBillingRowAsColombia()
    {
        var departmentId = Guid.CreateVersion7();
        var cityId = Guid.CreateVersion7();
        QuotationPartyDetails Billing(string? country) => new()
        {
            Name = "Sede administrativa",
            IdentificationNumber = "900123456",
            IdentificationType = PartyIdentificationType.Nit,
            Phone = "+573001112233",
            Email = "sede@x.co",
            Address = "Carrera 7",
            DepartmentId = departmentId,
            CityId = cityId,
            Country = country,
        };

        var old = Modules.Quotations.Application.QuotationHeaderSnapshot.Of(NewQuotation(Billing(null)));
        var saved = Modules.Quotations.Application.QuotationHeaderSnapshot.Of(NewQuotation(Billing("CO")));

        Assert.Equal(saved.Billing, old.Billing);
    }
}

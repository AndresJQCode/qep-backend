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
            Country = null, CityName = null, DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7(),
        });

        Assert.False(incomplete.Shipping!.IsComplete);
        Assert.True(complete.Shipping!.IsComplete);
        Assert.Null(complete.Shipping.Country);
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
}

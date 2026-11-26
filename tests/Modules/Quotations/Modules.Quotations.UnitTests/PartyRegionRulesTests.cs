using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// The geography half of the party region rule: the region must exist and belong to the party's country.
public sealed class PartyRegionRulesTests
{
    private static readonly Guid Madrid = Guid.CreateVersion7();
    private static readonly Guid Antioquia = Guid.CreateVersion7();

    private static StubQuotationGeographyLookup Geography() => new(
        new Dictionary<Guid, string>(),
        regions: new Dictionary<Guid, QuotationRegionRef> { [Madrid] = new(Madrid, "ES", "Madrid") });

    private static QuotationPartyDetails Party(string country, Guid? regionId) => new()
    {
        Name = "Bodega Madrid",
        Country = country,
        CityName = "Madrid",
        RegionId = regionId,
    };

    [Fact]
    public async Task RegionOfAnotherCountryIsRejected()
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => PartyRegionRules.EnsureAsync(
            Geography(), Party("FR", Madrid), "Parties.Billing", TestContext.Current.CancellationToken));

        Assert.Equal("quotations.party.region_invalid", error.Code);
        Assert.Contains("Parties.Billing.RegionId", error.FieldErrors.Keys);
    }

    [Fact]
    public async Task DepartmentIdAsRegionIsRejected()
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => PartyRegionRules.EnsureAsync(
            Geography(), Party("ES", Antioquia), "Parties.Shipping", TestContext.Current.CancellationToken));

        Assert.Equal("quotations.party.region_invalid", error.Code);
        Assert.Contains("Parties.Shipping.RegionId", error.FieldErrors.Keys);
    }

    [Fact]
    public async Task ForeignPartyWithItsRegionPasses()
    {
        var geography = Geography();

        await PartyRegionRules.EnsureAsync(
            geography,
            new QuotationParties(Party("es", Madrid), Party("ES", Madrid)),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, geography.FindRegionsCalls);
    }

    [Fact]
    public async Task APartyWithoutRegionNeverAsksGeography()
    {
        var geography = Geography();

        await PartyRegionRules.EnsureAsync(
            geography, new QuotationParties(Party("ES", null), null), TestContext.Current.CancellationToken);

        Assert.Equal(0, geography.FindRegionsCalls);
    }

    [Fact]
    public async Task ColombianPartyWithRegionIsLeftToTheDomainLane()
    {
        await PartyRegionRules.EnsureAsync(
            Geography(), Party("CO", Madrid), "Parties.Billing", TestContext.Current.CancellationToken);
    }

    // The aggregate discards the shipping of a store pickup and the billing of a final consumer, so a
    // bad region left on those parts must not fail the save (nor reach geography).
    [Fact]
    public async Task TheShippingOfAStorePickupIsNotChecked()
    {
        var geography = Geography();

        await PartyRegionRules.EnsureAsync(
            geography,
            new QuotationParties(null, Party("FR", Madrid), IsStorePickup: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, geography.FindRegionsCalls);
    }

    [Fact]
    public async Task TheBillingOfAFinalConsumerIsNotChecked()
    {
        var geography = Geography();

        await PartyRegionRules.EnsureAsync(
            geography,
            new QuotationParties(Party("FR", Madrid), null, BillsToFinalConsumer: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, geography.FindRegionsCalls);
    }
}

using Modules.Store.Domain;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

public sealed class StoreOrderRequestTests
{
    private static string CodeOf(Action act) => Assert.Throws<StoreDomainException>(act).Code;

    [Fact]
    public void AColombianPlaceNeedsItsIdsAndAForeignOneItsText()
    {
        Assert.Equal(
            StoreErrorCodes.CityInvalid,
            CodeOf(() => _ = new StorePlace("CO", Guid.NewGuid(), "Antioquia", null, "Medellín")));
        Assert.Equal(
            StoreErrorCodes.CityInvalid,
            CodeOf(() => _ = new StorePlace("ES", null, null, Guid.NewGuid(), "Madrid")));
        Assert.Equal(
            StoreErrorCodes.CityInvalid,
            CodeOf(() => _ = new StorePlace("ES", null, null, null, "  ")));

        var colombian = Colombia();
        var foreign = new StorePlace("es", null, null, null, " Madrid ");

        Assert.True(colombian.IsColombian);
        Assert.False(foreign.IsColombian);
        Assert.Equal("ES", foreign.Country);
        Assert.Equal("Madrid", foreign.CityName);
    }

    [Fact]
    public void AColombianPlaceNeedsBothNames()
    {
        Assert.Equal(
            StoreErrorCodes.CityInvalid,
            CodeOf(() => _ = new StorePlace("CO", Guid.NewGuid(), null, Guid.NewGuid(), "Medellín")));
        Assert.Equal(
            StoreErrorCodes.CityInvalid,
            CodeOf(() => _ = new StorePlace("CO", Guid.NewGuid(), "Antioquia", Guid.NewGuid(), "")));
    }

    [Fact]
    public void ShippingWithoutARecipientUsesTheBillingName()
    {
        var billing = Billing();

        Assert.Equal("Ana Pérez", Shipping().EffectiveRecipientName(billing));
        Assert.Equal("Luis", Shipping("  Luis ").EffectiveRecipientName(billing));
        Assert.Equal("Ana Pérez", Shipping("   ").EffectiveRecipientName(billing));
    }

    [Fact]
    public void ABillingBlockBeyondTheLimitsIsRejected()
    {
        Assert.Equal(
            StoreErrorCodes.ValidationFailed,
            CodeOf(() => _ = new StoreRequestBilling(
                new string('a', 151), "CC", "1", "300", "+57300", "a@b.co", "Calle 1", Colombia())));
        Assert.Equal(
            StoreErrorCodes.ValidationFailed,
            CodeOf(() => _ = new StoreRequestBilling("Ana", "RUT", "1", "300", "+57300", "a@b.co", "Calle 1", Colombia())));
        Assert.Equal(
            StoreErrorCodes.ValidationFailed,
            CodeOf(() => _ = new StoreRequestBilling("Ana", "CC", "1", "300", "+57300", "a@b.co", "", Colombia())));
    }

    [Fact]
    public void ABillingBlockIsTrimmed()
    {
        var billing = new StoreRequestBilling(" Ana ", "CC", " 12 ", " 300 ", "+57300", " a@b.co ", " Calle 1 ", Colombia());

        Assert.Equal("Ana", billing.Name);
        Assert.Equal("12", billing.IdentificationNumber);
        Assert.Equal("a@b.co", billing.Email);
        Assert.Equal("Calle 1", billing.Address);
    }

    [Fact]
    public void ARequestWithoutShippingKeepsItNull()
    {
        var request = Request(shipping: null);

        Assert.Null(request.Shipping);
        Assert.NotNull(Request(shipping: Shipping()).Shipping);
    }

    [Theory]
    [InlineData(7L, "T-0007")]
    [InlineData(12345L, "T-12345")]
    public void TheDisplayNumberIsPaddedToFourDigits(long number, string expected) =>
        Assert.Equal(expected, Request(number).DisplayNumber);

    [Fact]
    public void ANewRequestStartsNewWithoutLease()
    {
        var request = Request();

        Assert.Equal(StoreOrderRequestStatus.New, request.Status);
        Assert.False(request.IsBeingTaken(Now));
        Assert.Single(request.Lines);
    }

    [Fact]
    public void ARequestNeedsLinesAndAThreeLetterCurrency()
    {
        Assert.Equal(
            StoreErrorCodes.ValidationFailed,
            CodeOf(() => StoreOrderRequest.Create(
                Guid.NewGuid(), TenantId, 1, Guid.NewGuid(), Billing(), null, null, "COP",
                StorePricingMode.RetailOnly, [], 0m, 0m, 0m, 0m, Consent(), Now)));
        Assert.Equal(
            StoreErrorCodes.ValidationFailed,
            CodeOf(() => StoreOrderRequest.Create(
                Guid.NewGuid(), TenantId, 1, Guid.NewGuid(), Billing(), null, null, "CO",
                StorePricingMode.RetailOnly, Lines(), 1m, 0m, 0m, 1m, Consent(), Now)));
    }

    [Fact]
    public void HasScaleDiscountIsTrueOnlyWhenALineCarriesOne()
    {
        Assert.False(Request().HasScaleDiscount);
        Assert.True(Request(discountPercentage: 5m).HasScaleDiscount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AZeroOrNegativeLeaseIsRejected(int seconds)
    {
        var request = Request();

        Assert.Throws<ArgumentOutOfRangeException>(() => request.BeginConversion(Member, Now, TimeSpan.FromSeconds(seconds)));
        Assert.Null(request.LeaseMemberId);
    }

    [Fact]
    public void OnlyANewRequestCanBeConvertedOrDiscarded()
    {
        var converted = Request();
        converted.MarkConverted(Guid.NewGuid(), Guid.NewGuid(), "P-1", 20000m, Member, Now);

        Assert.Equal(StoreErrorCodes.NotNew, CodeOf(() => converted.BeginConversion(Member, Now, Lease)));
        Assert.Equal(
            StoreErrorCodes.NotNew,
            CodeOf(() => converted.MarkConverted(Guid.NewGuid(), Guid.NewGuid(), "P-2", 1m, Member, Now)));
        Assert.Equal(
            StoreErrorCodes.NotNew,
            CodeOf(() => converted.Discard(Member, StoreDiscardReason.Spam, null, Now)));

        var discarded = Request();
        discarded.Discard(Member, StoreDiscardReason.Spam, null, Now);

        Assert.Equal(StoreOrderRequestStatus.Discarded, discarded.Status);
        Assert.Equal(StoreErrorCodes.NotNew, CodeOf(() => discarded.BeginConversion(Member, Now, Lease)));
        Assert.Equal(
            StoreErrorCodes.NotNew,
            CodeOf(() => discarded.MarkConverted(Guid.NewGuid(), Guid.NewGuid(), "P-3", 1m, Member, Now)));
    }

    [Fact]
    public void AnotherMembersRunningLeaseBlocksTakeAndDiscard()
    {
        var request = Request();
        request.BeginConversion(Member, Now, Lease);
        var during = Now.AddSeconds(30);

        Assert.True(request.IsBeingTaken(during));
        Assert.Equal(StoreErrorCodes.BeingTaken, CodeOf(() => request.BeginConversion(OtherMember, during, Lease)));
        Assert.Equal(
            StoreErrorCodes.BeingTaken,
            CodeOf(() => request.Discard(OtherMember, StoreDiscardReason.Spam, null, during)));
    }

    // Revision final: Descartar con el lease vivo del propio asesor dejaba un pedido huerfano (la toma ya creo el pedido).
    [Fact]
    public void AnyLiveLeaseBlocksDiscardEvenTheCallersOwn()
    {
        var request = Request();
        request.BeginConversion(Member, Now, Lease);

        Assert.Equal(
            StoreErrorCodes.BeingTaken,
            CodeOf(() => request.Discard(Member, StoreDiscardReason.Spam, null, Now.AddSeconds(30))));
        Assert.Equal(StoreOrderRequestStatus.New, request.Status);
    }

    // Un doble clic o un reintento del mismo asesor mientras la primera toma sigue corriendo no puede abrir una segunda: si una de las
    // dos falla, soltaría el lease que la otra todavía usa (revisión de la tarea 12).
    [Fact]
    public void TheSameMemberCannotReenterALiveLease()
    {
        var request = Request();
        request.BeginConversion(Member, Now, Lease);

        Assert.Equal(StoreErrorCodes.BeingTaken, CodeOf(() => request.BeginConversion(Member, Now.AddSeconds(90), Lease)));
        Assert.False(request.IsBeingTaken(Now + Lease));
    }

    [Fact]
    public void TheSameMemberRetakesOnceItsLeaseExpired()
    {
        var request = Request();
        request.BeginConversion(Member, Now, Lease);
        var after = Now + Lease + TimeSpan.FromSeconds(1);

        request.BeginConversion(Member, after, Lease);

        Assert.True(request.IsBeingTaken(after));
    }

    [Fact]
    public void AnExpiredLeaseCanBeTakenByAnotherMember()
    {
        var request = Request();
        request.BeginConversion(Member, Now, Lease);
        var after = Now + Lease + TimeSpan.FromSeconds(1);

        Assert.False(request.IsBeingTaken(after));
        request.BeginConversion(OtherMember, after, Lease);

        Assert.True(request.IsBeingTaken(after));
        Assert.Equal(OtherMember, request.LeaseMemberId);
    }

    [Fact]
    public void ReleasingTheConversionFreesTheLease()
    {
        var request = Request();
        request.BeginConversion(Member, Now, Lease);

        request.ReleaseConversion();

        Assert.False(request.IsBeingTaken(Now));
        Assert.Null(request.LeaseMemberId);
        request.BeginConversion(OtherMember, Now, Lease);
    }

    [Fact]
    public void DiscardingWithOtherRequiresANote()
    {
        var request = Request();

        Assert.Equal(
            StoreErrorCodes.ValidationFailed,
            CodeOf(() => request.Discard(Member, StoreDiscardReason.Other, "  ", Now)));
        Assert.Equal(StoreOrderRequestStatus.New, request.Status);

        request.Discard(Member, StoreDiscardReason.Other, " Cliente se arrepintió ", Now);

        Assert.Equal(StoreDiscardReason.Other, request.DiscardReason);
        Assert.Equal("Cliente se arrepintió", request.DiscardNote);
        Assert.Equal(Member, request.DiscardedBy);
        Assert.Equal(Now, request.DiscardedAt);
    }

    [Fact]
    public void DiscardingBySpamNeedsNoNoteAndTakesOverAnExpiredLease()
    {
        var request = Request();
        request.BeginConversion(Member, Now, Lease);

        request.Discard(OtherMember, StoreDiscardReason.Spam, null, Now + Lease + TimeSpan.FromSeconds(1));

        Assert.Equal(StoreOrderRequestStatus.Discarded, request.Status);
        Assert.Null(request.LeaseMemberId);
    }

    [Fact]
    public void ConvertingFreezesTheOrderAndClearsTheLease()
    {
        var request = Request(total: 20000m);
        request.BeginConversion(Member, Now, Lease);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        request.MarkConverted(orderId, customerId, "P-9", 20500m, Member, Now.AddMinutes(1));

        Assert.Equal(StoreOrderRequestStatus.Converted, request.Status);
        Assert.Equal(orderId, request.OrderId);
        Assert.Equal(customerId, request.CustomerId);
        Assert.Equal("P-9", request.OrderNumber);
        Assert.Equal(Member, request.ConvertedBy);
        Assert.Equal(500m, request.TotalDifference);
        Assert.False(request.IsBeingTaken(Now.AddMinutes(1)));
        Assert.Null(request.LeaseMemberId);
    }

    [Fact]
    public void TheTotalDifferenceIsNullWhenTheTotalsMatch()
    {
        var request = Request(total: 20000m);

        request.MarkConverted(Guid.NewGuid(), Guid.NewGuid(), "P-9", 20000m, Member, Now);

        Assert.Null(request.TotalDifference);
    }

    [Fact]
    public void ANegativeDifferenceIsKept()
    {
        var request = Request(total: 20000m);

        request.MarkConverted(Guid.NewGuid(), Guid.NewGuid(), "P-9", 19000m, Member, Now);

        Assert.Equal(-1000m, request.TotalDifference);
    }

    [Fact]
    public void EveryStateChangeBumpsTheVersion()
    {
        var request = Request();
        var start = request.Version;

        request.BeginConversion(Member, Now, Lease);
        Assert.True(request.Version > start);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void OnlyRetailOnlyIsRetail(bool retailOnly, bool expected) =>
        Assert.Equal(
            expected,
            StorePricingModes.IsRetail(retailOnly ? StorePricingMode.RetailOnly : StorePricingMode.QuantityScales));
}

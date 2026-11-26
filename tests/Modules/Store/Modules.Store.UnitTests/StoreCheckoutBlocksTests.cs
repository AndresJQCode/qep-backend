using Modules.Store.Domain;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

/// <summary>Los bloques del checkout (lugar, facturación y envío) validan y normalizan antes de que se arme el pedido.</summary>
public sealed class StoreCheckoutBlocksTests
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

    private static StorePlace Spain() => new("ES", null, null, null, "Madrid");

    [Fact]
    public void ABillingDocumentMustBeAllowedForItsCountry()
    {
        Assert.Equal(
            StoreErrorCodes.DocumentTypeNotAllowed,
            CodeOf(() => _ = new StoreRequestBilling("Ana", "CC", "1", "612345678", "+34612345678", "a@b.es", "Calle Mayor 1", Spain())));
        Assert.Equal("NIF", new StoreRequestBilling("Ana", " nif ", "1", "612345678", "+34612345678", "a@b.es", "Calle Mayor 1", Spain()).IdentificationType);
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
}

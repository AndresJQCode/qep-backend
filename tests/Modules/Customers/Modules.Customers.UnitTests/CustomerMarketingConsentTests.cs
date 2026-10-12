using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-11 §4.1 y decisión 10: la bandera <c>marketing_opt_out</c> la prende o apaga una persona
/// (<c>manual</c>) y se prende sola cuando Meta responde 131050 (<c>meta</c>).</summary>
public sealed class CustomerMarketingConsentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => phone;

        public bool IsKnownRegion(string regionCode) => true;

        public string? RegionOf(string? e164) => null;
    }

    private static Customer NewCustomer() =>
        Customer.CreateIncomplete(
            CustomerId.New(), Guid.CreateVersion7(), "Laura", "+573001234567", "CO", "CO.1", Now, new FakeNormalizer());

    [Fact]
    public void ACustomerStartsAcceptingMarketing()
    {
        var customer = NewCustomer();

        Assert.False(customer.MarketingOptOut);
        Assert.Null(customer.MarketingOptOutAt);
        Assert.Null(customer.MarketingOptOutSource);
    }

    [Fact]
    public void OptingOutRecordsWhenAndBy()
    {
        var customer = NewCustomer();

        var changed = customer.OptOutOfMarketing(MarketingConsentSource.Manual, Now);

        Assert.True(changed);
        Assert.True(customer.MarketingOptOut);
        Assert.Equal(Now, customer.MarketingOptOutAt);
        Assert.Equal(MarketingConsentSource.Manual, customer.MarketingOptOutSource);
    }

    [Fact]
    public void OptingOutAgainKeepsTheOriginalRecord()
    {
        var customer = NewCustomer();
        customer.OptOutOfMarketing(MarketingConsentSource.Meta, Now);

        var changed = customer.OptOutOfMarketing(MarketingConsentSource.Manual, Now.AddDays(1));

        Assert.False(changed);
        Assert.Equal(Now, customer.MarketingOptOutAt);
        Assert.Equal(MarketingConsentSource.Meta, customer.MarketingOptOutSource);
    }

    [Fact]
    public void OptingInClearsTheRecord()
    {
        var customer = NewCustomer();
        customer.OptOutOfMarketing(MarketingConsentSource.Manual, Now);

        var changed = customer.OptInToMarketing();

        Assert.True(changed);
        Assert.False(customer.MarketingOptOut);
        Assert.Null(customer.MarketingOptOutAt);
        Assert.Null(customer.MarketingOptOutSource);
    }

    [Fact]
    public void OptingInWhenAlreadyAcceptingChangesNothing()
    {
        Assert.False(NewCustomer().OptInToMarketing());
    }

    [Fact]
    public void ConsentDoesNotTouchTheCustomerVersion()
    {
        // La ficha se edita con la versión cargada: un interruptor aparte no debe darle un 412 a quien la tiene abierta.
        var customer = NewCustomer();
        var before = customer.Version;

        customer.OptOutOfMarketing(MarketingConsentSource.Manual, Now);

        Assert.Equal(before, customer.Version);
    }

    [Theory]
    [InlineData(MarketingConsentSource.Manual, "manual")]
    [InlineData(MarketingConsentSource.Meta, "meta")]
    [InlineData(MarketingConsentSource.Button, "button")]
    public void TheSourceTravelsByLowercaseName(MarketingConsentSource source, string wire)
    {
        Assert.Equal(wire, source.ToWireValue());
        Assert.Equal(source, MarketingConsentSourceParser.Parse(wire));
    }
}

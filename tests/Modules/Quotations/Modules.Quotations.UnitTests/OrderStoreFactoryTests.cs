using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Los pedidos que nacen de la tienda pública: <see cref="Order.CreateFromStore"/> los guarda y
/// <see cref="Order.QuoteForStore"/> sólo los calcula.</summary>
public sealed class OrderStoreFactoryTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
    };

    private static OrderHeader Header() =>
        new(Guid.CreateVersion7(), Advisor, "COP", null, IsRetail: true, null, false, []);

    private static OrderLineInput Line() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static QuotationPartyDetails Billing() => new()
    {
        Name = "Ana Pérez",
        IdentificationNumber = "1020304050",
        IdentificationType = PartyIdentificationType.Cc,
        PersonType = PartyPersonType.Natural,
        Phone = "+573001112233",
        Email = "ana@example.com",
        Address = "Calle 1 # 2-3",
        DepartmentId = Guid.CreateVersion7(),
        CityId = Guid.CreateVersion7(),
    };

    private static QuotationPartyDetails Shipping() =>
        Billing() with { IdentificationNumber = null, IdentificationType = null, PersonType = null };

    private static QuotationParties Parties(
        QuotationPartyDetails? shipping = null, bool? vatSurplus = false) =>
        new(Billing(), shipping ?? Shipping(), BillingVatSurplus: vatSurplus);

    private static Order NewStoreOrder(QuotationParties parties) =>
        Order.CreateFromStore(
            OrderId.New(), TenantId, "PED-2026-0001", Header(), parties, Account, [Line()], null, Advisor, Now,
            "store-checkout:abc");

    [Fact]
    public void CreateFromStoreKeepsTheBuyerAccount()
    {
        var buyer = Guid.CreateVersion7();
        var order = Order.CreateFromStore(
            OrderId.New(), TenantId, "PED-2026-0001", Header(), Parties(), Account, [Line()], null, Advisor, Now,
            "store-checkout:abc", buyerAccountId: buyer);
        Assert.Equal(buyer, order.BuyerAccountId);
    }

    [Fact]
    public void CreateFromStoreWithoutABuyerLeavesItNull() =>
        Assert.Null(NewStoreOrder(Parties()).BuyerAccountId);

    [Fact]
    public void AStoreOrderKeepsAForeignTaxId()
    {
        var billing = Billing() with
        {
            IdentificationType = PartyIdentificationType.TaxId,
            Country = "US",
            CityName = "Miami",
            DepartmentId = null,
            CityId = null,
        };

        var order = NewStoreOrder(Parties() with { Billing = billing });

        var party = Assert.Single(order.Parties, p => p.Role == QuotationPartyRole.Billing);
        Assert.Equal((PartyIdentificationType.TaxId, "US"), (party.IdentificationType, party.Country));
    }

    [Fact]
    public void CreateFromStoreSetsSourceReferenceAndKey()
    {
        var order = NewStoreOrder(Parties());

        Assert.Equal(
            (OrderSource.Store, OrderPaymentStatus.PaymentPending, (string?)null, "store-checkout:abc"),
            (order.Source, order.PaymentStatus, order.ExternalReference, order.ExternalKey));
        Assert.Empty(order.PaymentProofs);
    }

    [Fact]
    public void AStoreOrderIsNotExternal()
    {
        var order = NewStoreOrder(Parties());

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachExternalShipping(Shipping(), "Envío:", Now));

        Assert.Equal("order.order.source_invalid", error.Code);
    }

    [Fact]
    public void CreateExternalRejectsTheStoreSource()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.CreateExternal(
                OrderId.New(), TenantId, "PED-2026-0001", Header(), Parties(), Account, [Line()],
                OrderPaymentStatus.PaymentPending, null, Advisor, [], Now, OrderSource.Store, "T-0001",
                ShippingDetails.None, null, null, OrderLineCosts.None));

        Assert.Equal(ExternalOrderErrors.SourceInvalid, error.Code);
    }

    [Fact]
    public void QuoteForStoreHasNoClientAndIsNeverAManualOrder()
    {
        var quote = Order.QuoteForStore(TenantId, "COP", isRetail: true, [Line()], Now);

        Assert.Equal((Guid.Empty, OrderSource.Store), (quote.ClientId, quote.Source));
        Assert.Null(quote.BillingAccount);
        Assert.Equal(23_800m, quote.NetTotal);
    }

    [Fact]
    public void AStoreOrderIsDeliveredWithBothParties()
    {
        var order = NewStoreOrder(Parties());

        Assert.False(order.IsStorePickup);
        Assert.Equal(
            [QuotationPartyRole.Billing, QuotationPartyRole.Shipping],
            order.Parties.Select(party => party.Role).Order());
    }

    [Fact]
    public void AStoreOrderWithoutAPartyEmailIsRejected()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewStoreOrder(Parties(Shipping() with { Email = null })));

        Assert.Equal(ExternalOrderErrors.ShippingPartyIncomplete, error.Code);
    }

    [Fact]
    public void AStoreOrderStillRequiresTheBillingTaxProfile()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewStoreOrder(Parties(vatSurplus: null)));

        Assert.Equal("order.billing.tax_profile_required", error.Code);
    }

    [Fact]
    public void AStoreOrderStillRequiresTheShippingAddress()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewStoreOrder(Parties(Shipping() with { CityId = null })));

        Assert.Equal(ExternalOrderErrors.ShippingPartyIncomplete, error.Code);
    }
}

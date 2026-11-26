using BuildingBlocks.Domain.Catalog;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>La entrega editable en «Editar pedido» (spec 2026-11-24 §10): recoger o enviar y a dónde, sólo mientras el pedido
/// no esté facturado ni preparado.</summary>
public sealed class OrderDeliveryTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
        Country = "CO",
    };

    private static OrderHeader Header => new(ClientId, Advisor, "COP", null, false, null, false, []);

    private static OrderLineInput Line =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static QuotationPartyDetails Party => new()
    {
        Name = "Bodega Norte",
        Phone = "3001234567",
        Email = "bodega@correo.co",
        Address = "Cra 7 # 12-30",
        DepartmentId = Guid.CreateVersion7(),
        CityId = Guid.CreateVersion7(),
    };

    private static Order NewOrder(
        QuotationPartyDetails? shipping = null, bool isStorePickup = false, ShippingDetails? freight = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header,
            new QuotationParties(null, shipping, IsStorePickup: isStorePickup), Account, freight ?? ShippingDetails.None,
            [Line], null, Advisor, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    private static string CodeOf(Action action) => Assert.Throws<QuotationsDomainException>(action).Code;

    [Fact]
    public void AnInvoicedOrderRejectsTheDeliveryChange()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, order.WithoutCosts(), Now);
        order.Invoice(Advisor, requireFullPayment: true, Now);

        Assert.Equal("order.order.already_invoiced", CodeOf(() => order.SetDelivery(true, null, Advisor, Now)));
        Assert.Equal("order.order.already_invoiced", CodeOf(order.EnsureDeliveryEditable));
        Assert.False(order.IsStorePickup);
    }

    [Fact]
    public void APreparedOrderRejectsTheDeliveryChange()
    {
        var order = NewOrder();
        order.Prepare(Advisor, Now);

        Assert.Equal("order.order.already_prepared", CodeOf(() => order.SetDelivery(true, null, Advisor, Now)));
        Assert.Equal("order.order.already_prepared", CodeOf(order.EnsureDeliveryEditable));
        Assert.False(order.IsStorePickup);
    }

    [Fact]
    public void InvoicedWinsOverPrepared()
    {
        var order = NewOrder();
        order.MarkPaid(Advisor, requirePaymentProof: false, order.WithoutCosts(), Now);
        order.Prepare(Advisor, Now);
        order.Invoice(Advisor, requireFullPayment: true, Now);

        Assert.Equal("order.order.already_invoiced", CodeOf(() => order.SetDelivery(true, null, Advisor, Now)));
    }

    [Fact]
    public void SwitchingToPickupDropsThePartyAndClearsShippingAndCashOnDelivery()
    {
        var order = NewOrder(
            Party, freight: new ShippingDetails(
                Guid.CreateVersion7(), "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, true));
        var version = order.Version;
        var totalWithFreight = order.Total;

        var changed = order.SetDelivery(true, null, Advisor, Now);

        Assert.True(changed);
        Assert.True(order.IsStorePickup);
        Assert.Null(order.Shipping);
        Assert.Equal(ShippingDetails.None, order.ShippingDetails);
        Assert.Equal(totalWithFreight - 15_000m, order.Total);
        Assert.True(order.Version > version);
    }

    [Fact]
    public void SwitchingToSendWithAPartyCreatesTheRow()
    {
        var order = NewOrder(isStorePickup: true);
        var party = Party;

        Assert.True(order.SetDelivery(false, party, Advisor, Now));

        Assert.False(order.IsStorePickup);
        Assert.Equal(party, order.Shipping!.ToDetails());
    }

    [Fact]
    public void SwitchingToSendWithNullUsesTheCustomer()
    {
        var order = NewOrder(isStorePickup: true);

        Assert.True(order.SetDelivery(false, null, Advisor, Now));

        Assert.False(order.IsStorePickup);
        Assert.Null(order.Shipping);
    }

    [Fact]
    public void PickupWithAPartyIsRejected()
    {
        var order = NewOrder();

        Assert.Equal(
            "order.delivery.shipping_party_on_pickup", CodeOf(() => order.SetDelivery(true, Party, Advisor, Now)));
        Assert.False(order.IsStorePickup);
    }

    [Fact]
    public void TheSameDeliveryIsNotAChange()
    {
        var party = Party;
        var order = NewOrder(party);
        var version = order.Version;

        Assert.False(order.SetDelivery(false, party, Advisor, Now.AddHours(1)));

        Assert.Equal(version, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Fact]
    public void EditingThePartyModifiesTheExistingRow()
    {
        var party = Party;
        var order = NewOrder(party);
        var rowId = order.Shipping!.Id;

        Assert.True(order.SetDelivery(false, party with { Address = "Calle 1 # 2-3" }, Advisor, Now));

        Assert.Equal(rowId, order.Shipping!.Id);
        Assert.Equal("Calle 1 # 2-3", order.Shipping.Address);
    }

    [Fact]
    public void AnIncompleteShippingPartyIsRejected()
    {
        var order = NewOrder(isStorePickup: true);
        var version = order.Version;
        QuotationPartyDetails[] incompleteParties =
            [new QuotationPartyDetails(), Party with { CityId = null }, Party with { Email = null }];

        foreach (var incomplete in incompleteParties)
        {
            Assert.Equal(
                "order.shipping.party_incomplete", CodeOf(() => order.SetDelivery(false, incomplete, Advisor, Now)));
        }

        Assert.True(order.IsStorePickup);
        Assert.Null(order.Shipping);
        Assert.Equal(version, order.Version);
    }

    // El cálculo previo se hace mientras la persona teclea.
    [Fact]
    public void AnIncompleteShippingPartyIsAcceptedWithoutEnforcingCompleteness()
    {
        var order = NewOrder(isStorePickup: true);

        Assert.True(order.SetDelivery(
            false, new QuotationPartyDetails { Name = "Bodega Norte" }, Advisor, Now, enforceCompleteness: false));

        Assert.Equal("Bodega Norte", order.Shipping!.Name);
        Assert.False(order.Shipping.IsComplete);
    }

    [Fact]
    public void ThePostalCodeTravelsWithTheParty()
    {
        var order = NewOrder(isStorePickup: true);

        order.SetDelivery(false, Party with { PostalCode = " 050021 " }, Advisor, Now);

        Assert.Equal("050021", order.Shipping!.PostalCode);

        var error = Assert.Throws<QuotationsDomainException>(() => order.SetDelivery(
            false, Party with { PostalCode = new string('1', QuotationPartyDetails.PostalCodeMaxLength + 1) }, Advisor, Now));

        Assert.Equal("quotations.party.postal_code_too_long", error.Code);
        Assert.Contains("Delivery.Shipping.PostalCode", error.FieldErrors.Keys);
    }
}

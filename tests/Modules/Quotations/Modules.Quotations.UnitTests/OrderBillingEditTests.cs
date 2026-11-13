using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Cambiar a quién se le factura en un pedido pendiente (spec 2026-10-26, «Editar pedido»).</summary>
public sealed class OrderBillingEditTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
        Country = "CO",
    };

    private static OrderHeader Header => new(ClientId, Advisor, "COP", null, false, null, false, []);

    private static OrderHeader WithholdingHeader => Header with { Withholdings = [TestWithholdings.RetefuenteCo] };

    private static OrderLineInput Line =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static QuotationPartyDetails Row(string name = "Otra S.A.S.") => new()
    {
        Name = name,
        IdentificationNumber = "900123456",
        PersonType = PartyPersonType.Juridica,
        IdentificationType = PartyIdentificationType.Nit,
        Phone = "3001234567",
        Email = "otra@example.com",
        Address = "Calle 1 # 2-3",
        DepartmentId = Guid.CreateVersion7(),
        CityId = Guid.CreateVersion7(),
    };

    private static Order NewOrder(QuotationParties? parties = null, OrderHeader? header = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", header ?? Header, parties ?? QuotationParties.Empty,
            Account, new ShippingDetails(null, null, null, 0m, 0, false), [Line],
            null, Advisor, [], Now);

    private static Order OrderWithRow(QuotationPartyDetails? row = null) =>
        NewOrder(QuotationParties.Empty with
        {
            Billing = row ?? Row(),
            BillingVatSurplus = false,
        });

    // El pedido tiene una sola fuente de tasas (spec impuestos por tipo §7.5): cambiar a quién se le factura no las
    // toca; sólo SetWithholdings las cambia.
    [Fact]
    public void SwitchingToAnOwnBillingPartyKeepsTheFrozenWithholdings()
    {
        var order = NewOrder(header: WithholdingHeader);
        var retention = order.RetentionAmount;
        Assert.True(retention > 0m);
        var version = order.Version;

        var changed = order.SetBilling(Row(), false, false, partyVatSurplus: false, Advisor, Now);

        Assert.True(changed);
        Assert.Equal(retention, order.RetentionAmount);
        Assert.Equal(TestWithholdings.RetefuenteCo, Assert.Single(order.Withholdings).Rate);
        Assert.Equal(version + 1, order.Version);
    }

    [Fact]
    public void SwitchingToTheFinalConsumerDropsTheRowAndTheRetention()
    {
        var order = NewOrder(
            QuotationParties.Empty with { Billing = Row(), BillingVatSurplus = false }, WithholdingHeader);
        Assert.True(order.RetentionAmount > 0m);

        order.SetBilling(null, true, false, null, Advisor, Now);

        Assert.Null(order.Billing);
        Assert.True(order.BillsToFinalConsumer);
        Assert.Equal(0m, order.RetentionAmount);
        Assert.Empty(order.AppliedWithholdings);
        // La foto no se pierde: dejar de facturar a consumidor final la vuelve a aplicar.
        Assert.Single(order.Withholdings);
    }

    [Fact]
    public void EditingTheExistingRowKeepsItsId()
    {
        var order = OrderWithRow();
        var id = order.Billing!.Id;

        order.SetBilling(Row("Tercera S.A.S."), false, false, false, Advisor, Now);

        Assert.Equal(id, order.Billing!.Id);
        Assert.Equal("Tercera S.A.S.", order.Billing.Name);
    }

    [Fact]
    public void AFinalConsumerWithARowIsAConflict()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetBilling(Row(), true, false, false, Advisor, Now));

        Assert.Equal("order.billing.final_consumer_conflict", error.Code);
    }

    [Fact]
    public void ARowWithoutIdentificationTypeIsRejected()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetBilling(Row() with { IdentificationType = null }, false, false, false, Advisor, Now));

        Assert.Equal("order.billing.identification_required", error.Code);
    }

    [Fact]
    public void APreparedOrderCannotChangeItsBilling()
    {
        var order = NewOrder();
        order.Prepare(Advisor, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetBilling(Row(), false, false, false, Advisor, Now));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    [Fact]
    public void TheSameBillingIsANoOp()
    {
        var row = Row();
        var order = OrderWithRow(row);
        var version = order.Version;

        var changed = order.SetBilling(row, false, false, false, Advisor, Now);

        Assert.False(changed);
        Assert.Equal(version, order.Version);
    }
}

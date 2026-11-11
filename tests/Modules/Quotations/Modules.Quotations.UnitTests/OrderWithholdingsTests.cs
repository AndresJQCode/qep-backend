using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El pedido congela sus tasas de retención (spec impuestos por tipo §7.5–§7.8, D21–D23): hereda la fuente vigente
/// de la cotización, los selectores lo cambian sólo mientras está pendiente y la empresa decide qué aplica.
/// </summary>
public sealed class OrderWithholdingsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationBillingAccount ColombianAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "CO",
    };

    private static readonly WithholdingRate Retefuente = TestWithholdings.RetefuenteCo;

    private static readonly WithholdingRate RetefuenteServicios =
        new(Guid.CreateVersion7(), "Retefuente servicios 4 %", "Retefuente", "CO", 4m, null, null, null);

    private static readonly WithholdingRate ReteIca =
        new(Guid.CreateVersion7(), "ReteICA Bogotá 0,966 %", "ReteICA", "CO", 0.966m, null, null, null);

    private static readonly WithholdingRate RetefuenteSpain =
        new(Guid.CreateVersion7(), "Retención IRPF 15 %", "Retefuente", "ES", 15m, null, null, null);

    // 4 UVT x 50.000 = 200.000: con 100.000 de base no aplica.
    private static readonly WithholdingRate ReteIcaWithMinimumBase =
        new(Guid.CreateVersion7(), "ReteICA con base", "ReteICA", "CO", 0.966m, 4m, "UVT", 50_000m);

    private static readonly QuotationPartyDetails OwnBilling = new()
    {
        Name = "Sede administrativa",
        IdentificationNumber = "1020304050",
        IdentificationType = PartyIdentificationType.Nit,
        Phone = "3105550134",
        Email = "compras@sede.co",
        Address = "Calle 10 # 45-12",
        DepartmentId = Guid.CreateVersion7(),
        CityId = Guid.CreateVersion7(),
    };

    private static OrderHeader Header(params WithholdingRate[] withholdings) =>
        new(ClientId, AdvisorId, "COP", null, IsRetail: false, null, CustomerVatSurplus: false, withholdings);

    // 119.000 con el IVA del 19 % adentro: base sin IVA 100.000.
    private static OrderLineInput Line() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static Order NewOrder(
        OrderHeader? header = null,
        QuotationParties? parties = null,
        QuotationBillingAccount? account = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", header ?? Header(Retefuente), parties ?? QuotationParties.Empty,
            account ?? ColombianAccount, ShippingDetails.None, [Line()], OrderPaymentStatus.PaymentPending, null,
            AdvisorId, [], Now);

    private static Order PreviewWithoutAccount(params WithholdingRate[] withholdings) =>
        Order.Preview(
            OrderId.New(), TenantId, "PED-2026-0001", Header(withholdings), QuotationParties.Empty, null,
            ShippingDetails.None, [Line()], OrderPaymentStatus.PaymentPending, null, AdvisorId, [], Now);

    private static (string Name, decimal Base, decimal Amount)[] Applied(Order order) =>
        [.. order.AppliedWithholdings.Select(withholding => (withholding.Name, withholding.BaseAmount, withholding.Amount))];

    [Fact]
    public void FromQuotationInheritsTheActiveSourceAsIs()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 12, 31), null, null,
            new QuotationParties(OwnBilling, null, BillingVatSurplus: false), ColombianAccount, "COP",
            customerVatSurplus: false, customerWithholdings: [Retefuente], AdvisorId, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, AdvisorId, Now);
        quotation.SetBillingWithholdings([ReteIca], AdvisorId, Now);
        quotation.ConvertToOrder(AdvisorId, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [quotation.Items.Single().VariantId] = new("ABC", "Aceite"),
        });

        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, OrderPaymentStatus.PaymentPending, null, AdvisorId, [], Now);

        // Con parte propia la fuente vigente son los selectores; la foto del cliente no viaja.
        var withholding = Assert.Single(order.Withholdings);
        Assert.Equal((WithholdingSource.Document, (short)0, ReteIca), (withholding.Source, withholding.Position, withholding.Rate));
        Assert.Equal(
            [.. quotation.AppliedWithholdings.Select(applied => (applied.Name, applied.BaseAmount, applied.Amount))],
            Applied(order));
        Assert.Equal((966m, quotation.NetTotal), (order.RetentionAmount, order.NetTotal));
    }

    [Fact]
    public void FromQuotationWithClientDataInheritsTheCustomerPhoto()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 12, 31), null, null,
            QuotationParties.Empty, ColombianAccount, "COP", customerVatSurplus: false,
            customerWithholdings: [Retefuente, ReteIca], AdvisorId, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, AdvisorId, Now);

        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, OrderPaymentStatus.PaymentPending, null, AdvisorId, [], Now);

        Assert.Equal([Retefuente, ReteIca], order.Withholdings.OrderBy(row => row.Position).Select(row => row.Rate));
        Assert.Equal([("Retefuente 2,5 %", 100_000m, 2_500m), ("ReteICA Bogotá 0,966 %", 100_000m, 966m)], Applied(order));
        Assert.Equal(quotation.RetentionAmount, order.RetentionAmount);
    }

    [Fact]
    public void ACreatedOrderFreezesTheHeaderRates()
    {
        var order = NewOrder(Header(Retefuente, ReteIca));

        Assert.Equal([("Retefuente 2,5 %", 100_000m, 2_500m), ("ReteICA Bogotá 0,966 %", 100_000m, 966m)], Applied(order));
        Assert.Equal(3_466m, order.RetentionAmount);
        Assert.Equal(order.Total - 3_466m, order.NetTotal);
    }

    [Fact]
    public void PendingAcceptsSelectorsAndNotPendingRejectsThem()
    {
        var order = NewOrder(Header(Retefuente));
        var version = order.Version;

        var changed = order.SetWithholdings([RetefuenteServicios, ReteIca], AdvisorId, Now);

        Assert.True(changed);
        Assert.Equal(version + 1, order.Version);
        Assert.Equal([("Retefuente servicios 4 %", 100_000m, 4_000m), ("ReteICA Bogotá 0,966 %", 100_000m, 966m)], Applied(order));
        Assert.Equal(4_966m, order.RetentionAmount);

        order.Approve(AdvisorId, Now);
        var error = Assert.Throws<QuotationsDomainException>(() => order.SetWithholdings([Retefuente], AdvisorId, Now));

        Assert.Equal("order.order.not_pending", error.Code);
        Assert.Equal(4_966m, order.RetentionAmount);
    }

    [Fact]
    public void EmptySelectorsAreAlwaysAcceptedAndApplyNone()
    {
        var order = NewOrder(Header(Retefuente));

        Assert.True(order.SetWithholdings([], AdvisorId, Now));

        Assert.Empty(order.Withholdings);
        Assert.Equal((0m, order.Total), (order.RetentionAmount, order.NetTotal));
    }

    [Fact]
    public void SettingTheSameSelectorsAgainChangesNothing()
    {
        var order = NewOrder(Header(Retefuente, ReteIca));
        var version = order.Version;

        Assert.False(order.SetWithholdings([Retefuente, ReteIca], AdvisorId, Now));

        Assert.Equal(version, order.Version);
    }

    [Fact]
    public void SelectorOfAnotherCountryIs422()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() => order.SetWithholdings([RetefuenteSpain], AdvisorId, Now));

        Assert.Equal("order.billing.withholding_tax_rate_invalid", error.Code);
        Assert.Equal(Retefuente, Assert.Single(order.Withholdings).Rate);
    }

    [Fact]
    public void SelectorsWithoutCompanyAre422()
    {
        var order = PreviewWithoutAccount();

        var error = Assert.Throws<QuotationsDomainException>(() => order.SetWithholdings([Retefuente], AdvisorId, Now));

        Assert.Equal("order.billing.withholding_tax_rate_invalid", error.Code);
    }

    [Fact]
    public void TwoSelectorsOfTheSameTypeAre422()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetWithholdings([Retefuente, RetefuenteServicios], AdvisorId, Now));

        Assert.Equal("order.billing.withholding_type_repeated", error.Code);
    }

    // Ruling de la Tarea 10: las filas se emparejan con el cálculo por tasa, no por posición. La primera no aplica
    // (base mínima) y la segunda sí: por posición, la segunda quedaría sin monto.
    [Fact]
    public void RowsArePairedWithTheirResultByTaxRateNotByPosition()
    {
        var order = NewOrder(Header(ReteIcaWithMinimumBase, Retefuente));

        var rows = order.Withholdings.OrderBy(row => row.Position).ToArray();
        Assert.Equal((false, 0m), (rows[0].Applied, rows[0].Amount));
        Assert.Equal((true, 2_500m), (rows[1].Applied, rows[1].Amount));
    }

    [Fact]
    public void SetBillingAccountEnablesWithholdings()
    {
        var order = PreviewWithoutAccount(Retefuente);
        Assert.Empty(order.AppliedWithholdings);
        Assert.Equal(0m, order.RetentionAmount);
        var version = order.Version;

        var changed = order.SetBillingAccount(ColombianAccount, AdvisorId, Now);

        Assert.True(changed);
        Assert.Equal(ColombianAccount, order.BillingAccount);
        Assert.Equal([("Retefuente 2,5 %", 100_000m, 2_500m)], Applied(order));
        Assert.Equal(version + 1, order.Version);
    }

    [Fact]
    public void SettingTheSameBillingAccountChangesNothing()
    {
        var order = NewOrder();
        var version = order.Version;

        Assert.False(order.SetBillingAccount(ColombianAccount, AdvisorId, Now));

        Assert.Equal(version, order.Version);
    }

    // Review Focus 5 (D23): la tasa guardada de otro país deja de aplicar, pero la foto se queda; el siguiente
    // guardado que la mande recibe withholding_tax_rate_invalid.
    [Fact]
    public void ChangingCompanyCountryStopsApplyingTheOldRate()
    {
        var order = NewOrder(Header(Retefuente));
        Assert.Equal(2_500m, order.RetentionAmount);

        order.SetBillingAccount(ColombianAccount with { CompanyId = Guid.CreateVersion7(), Country = "ES" }, AdvisorId, Now);

        Assert.Empty(order.AppliedWithholdings);
        Assert.Equal((0m, order.Total), (order.RetentionAmount, order.NetTotal));
        Assert.Equal(Retefuente, Assert.Single(order.Withholdings).Rate);
        var error = Assert.Throws<QuotationsDomainException>(() => order.SetWithholdings([Retefuente], AdvisorId, Now));
        Assert.Equal("order.billing.withholding_tax_rate_invalid", error.Code);
    }

    [Fact]
    public void CurrencyChangeOnSetBillingAccountIs422()
    {
        var order = NewOrder();
        var version = order.Version;

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetBillingAccount(ColombianAccount with { Currency = "USD" }, AdvisorId, Now));

        Assert.Equal("order.billing.currency_change_not_allowed", error.Code);
        Assert.Equal(("COP", ColombianAccount, version), (order.Currency, order.BillingAccount, order.Version));
    }

    [Fact]
    public void ANonPendingOrderCannotChangeItsBillingAccount()
    {
        var order = NewOrder();
        order.Approve(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetBillingAccount(ColombianAccount with { CompanyId = Guid.CreateVersion7() }, AdvisorId, Now));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    [Fact]
    public void EnsureBillingRulesAsksOnlyVatSurplus()
    {
        var unanswered = Assert.Throws<QuotationsDomainException>(() =>
            NewOrder(parties: new QuotationParties(OwnBilling, null, BillingVatSurplus: null)));
        Assert.Equal("order.billing.tax_profile_required", unanswered.Code);

        // Con el excedente contestado y sin ninguna retención, la parte propia pasa.
        var order = NewOrder(Header(), new QuotationParties(OwnBilling, null, BillingVatSurplus: false));

        Assert.NotNull(order.Billing);
        Assert.Empty(order.Withholdings);
        Assert.False(order.SetBilling(OwnBilling, false, false, partyVatSurplus: false, AdvisorId, Now));
    }

    [Fact]
    public void FinalConsumerAppliesNoneButKeepsThePhoto()
    {
        var order = NewOrder(Header(Retefuente), QuotationParties.Empty with { BillsToFinalConsumer = true });

        Assert.Empty(order.AppliedWithholdings);
        Assert.Equal(0m, order.RetentionAmount);
        Assert.Single(order.Withholdings);
    }
}

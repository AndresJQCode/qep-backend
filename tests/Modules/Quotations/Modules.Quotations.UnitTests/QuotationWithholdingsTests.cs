using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La cotización congela sus tasas de retención (spec impuestos por tipo §7.5–§7.8): de dónde salen, cuáles
/// aplican y qué exigen el envío, los selectores y la conversión.
/// </summary>
public sealed class QuotationWithholdingsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ValidUntil = new(2026, 12, 31);

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

    private static Quotation NewQuotation(
        IReadOnlyList<WithholdingRate>? customerWithholdings = null,
        QuotationBillingAccount? billingAccount = null,
        QuotationParties? parties = null,
        bool customerVatSurplus = false) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "COT-2026-0001",
            ClientId,
            AdvisorId,
            ValidUntil,
            paymentMethod: null,
            notes: null,
            parties ?? QuotationParties.Empty,
            billingAccount,
            "COP",
            customerVatSurplus,
            customerWithholdings ?? [],
            AdvisorId,
            Now);

    // 119.000 con el IVA del 19 % adentro: base sin IVA 100.000.
    private static void AddLine(Quotation quotation, decimal unitPrice = 119_000m) =>
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), quantity: 1, unitPrice,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

    private static void UseOwnBilling(Quotation quotation, QuotationBillingAccount? account, bool? vatSurplus = false) =>
        quotation.UpdateDetails(
            ValidUntil, null, null, new QuotationParties(OwnBilling, null, BillingVatSurplus: vatSurplus),
            account, null, isRetail: false, globalScaleFloor: null, AdvisorId, Now);

    private static void UseClientData(Quotation quotation, QuotationBillingAccount? account) =>
        quotation.UpdateDetails(
            ValidUntil, null, null, QuotationParties.Empty, account, null, isRetail: false, globalScaleFloor: null,
            AdvisorId, Now);

    [Fact]
    public void FinalConsumerAppliesNone()
    {
        var quotation = NewQuotation(
            [Retefuente], ColombianAccount, QuotationParties.Empty with { BillsToFinalConsumer = true });
        AddLine(quotation);

        Assert.Empty(quotation.AppliedWithholdings);
        Assert.Equal(0m, quotation.RetentionAmount);
        Assert.Equal(quotation.Total, quotation.NetTotal);
        // La foto no se pierde: desmarcar consumidor final la vuelve a aplicar.
        Assert.Single(quotation.CustomerWithholdings);
    }

    [Fact]
    public void UsdWithColombianCompanyAppliesNone()
    {
        var quotation = NewQuotation([Retefuente], ColombianAccount with { Currency = "USD" });
        AddLine(quotation, unitPrice: 100m);

        Assert.Equal("USD", quotation.Currency);
        Assert.Empty(quotation.AppliedWithholdings);
        Assert.Equal(0m, quotation.RetentionAmount);
    }

    // Review Focus 1: el borrador sin empresa conserva la foto pero no retiene; al elegir la empresa, aplica.
    [Fact]
    public void ADraftWithoutBillingAccountAppliesNoWithholdingAndAppliesItOnceChosen()
    {
        var quotation = NewQuotation([Retefuente], billingAccount: null);
        AddLine(quotation);

        Assert.Single(quotation.CustomerWithholdings);
        Assert.Empty(quotation.AppliedWithholdings);
        Assert.Equal(0m, quotation.RetentionAmount);

        UseClientData(quotation, ColombianAccount);

        var applied = Assert.Single(quotation.AppliedWithholdings);
        Assert.Equal(Retefuente, applied.Rate);
        Assert.Equal(100_000m, applied.BaseAmount);
        Assert.Equal(2_500m, applied.Amount);
        Assert.Equal(2_500m, quotation.RetentionAmount);
        Assert.Equal(116_500m, quotation.NetTotal);
    }

    [Fact]
    public void ClientDataUsesCustomerRates()
    {
        var quotation = NewQuotation([Retefuente, ReteIca], ColombianAccount);
        AddLine(quotation);

        Assert.Equal(
            [(Retefuente.TaxRateId, 2_500m), (ReteIca.TaxRateId, 966m)],
            quotation.AppliedWithholdings.Select(w => (w.TaxRateId, w.Amount)));
        Assert.Equal(3_466m, quotation.RetentionAmount);
        Assert.Equal(119_000m - 3_466m, quotation.NetTotal);
        Assert.All(quotation.CustomerWithholdings, w => Assert.Equal(WithholdingSource.Customer, w.Source));
        Assert.Equal([0, 1], quotation.CustomerWithholdings.Select(w => (int)w.Position));
    }

    [Fact]
    public void OwnBillingUsesSelectorsAndEmptySelectorsApplyNone()
    {
        var quotation = NewQuotation([Retefuente], ColombianAccount);
        AddLine(quotation);

        UseOwnBilling(quotation, ColombianAccount);

        // Con parte propia mandan los selectores, que nacen vacíos: no retiene aunque el cliente sí.
        Assert.Empty(quotation.BillingWithholdings);
        Assert.Empty(quotation.AppliedWithholdings);
        Assert.Equal(0m, quotation.RetentionAmount);

        Assert.True(quotation.SetBillingWithholdings([RetefuenteServicios], AdvisorId, Now));

        var applied = Assert.Single(quotation.AppliedWithholdings);
        Assert.Equal(WithholdingSource.Billing, applied.Source);
        Assert.Equal(4_000m, applied.Amount);
        Assert.Equal(4_000m, quotation.RetentionAmount);
        // La foto del cliente sigue ahí, sin aplicar.
        Assert.False(Assert.Single(quotation.CustomerWithholdings).Applied);
    }

    // Pasar de un modo de facturación al otro no borra la foto del otro modo (spec §7.5).
    [Fact]
    public void BackToClientDataRecoversTheCustomerPhoto()
    {
        var quotation = NewQuotation([Retefuente], ColombianAccount);
        AddLine(quotation);
        UseOwnBilling(quotation, ColombianAccount);
        quotation.SetBillingWithholdings([RetefuenteServicios], AdvisorId, Now);

        UseClientData(quotation, ColombianAccount);

        Assert.Equal(2_500m, Assert.Single(quotation.AppliedWithholdings).Amount);
        var selector = Assert.Single(quotation.BillingWithholdings);
        Assert.Equal(RetefuenteServicios, selector.Rate);
        Assert.False(selector.Applied);

        UseOwnBilling(quotation, ColombianAccount);

        Assert.Equal(4_000m, Assert.Single(quotation.AppliedWithholdings).Amount);
    }

    [Fact]
    public void OnlyRatesOfTheCompanyCountryApply()
    {
        var quotation = NewQuotation([RetefuenteSpain, ReteIca], ColombianAccount);
        AddLine(quotation);

        var applied = Assert.Single(quotation.AppliedWithholdings);
        Assert.Equal(ReteIca.TaxRateId, applied.TaxRateId);
        Assert.Equal(966m, quotation.RetentionAmount);
        Assert.Equal(2, quotation.CustomerWithholdings.Count());
    }

    // El cálculo devuelve sólo las que aplicaron: emparejar por posición le daría el monto de la segunda tasa
    // a la primera fila (ruling del plan, Tarea 10).
    [Fact]
    public void RowsArePairedWithTheirResultByTaxRateNotByPosition()
    {
        var quotation = NewQuotation([ReteIcaWithMinimumBase, Retefuente], ColombianAccount);
        AddLine(quotation);

        var rows = quotation.CustomerWithholdings.ToArray();
        Assert.False(rows[0].Applied);
        Assert.Equal(0m, rows[0].Amount);
        Assert.True(rows[1].Applied);
        Assert.Equal(2_500m, rows[1].Amount);
        Assert.Equal(100_000m, rows[1].BaseAmount);
    }

    [Fact]
    public void ChangeClientRecopiesRatesAndVatSurplusAndClearsSelectors()
    {
        var quotation = NewQuotation([Retefuente], ColombianAccount);
        AddLine(quotation);
        UseOwnBilling(quotation, ColombianAccount);
        quotation.SetBillingWithholdings([RetefuenteServicios], AdvisorId, Now);

        quotation.ChangeClient(Guid.CreateVersion7(), customerVatSurplus: true, [ReteIca], AdvisorId, Now);

        Assert.Equal([ReteIca], quotation.CustomerWithholdings.Select(w => w.Rate));
        Assert.Empty(quotation.BillingWithholdings);
        Assert.True(quotation.CustomerVatSurplus);
        Assert.Equal(966m, quotation.RetentionAmount);
        // Con excedente de IVA no se cobra IVA.
        Assert.Equal(0m, quotation.TaxAmount);
    }

    [Fact]
    public void SendWithoutBillingAccountIs422()
    {
        var quotation = NewQuotation([Retefuente], billingAccount: null);
        AddLine(quotation);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.Send(AdvisorId, Now, QuotationProductSnapshot.None));

        Assert.Equal("quotation.billing.account_required", error.Code);
        Assert.Equal(QuotationStatus.Draft, quotation.Status);
    }

    [Fact]
    public void DraftWithoutAccountCanStillBeSaved()
    {
        var quotation = NewQuotation([Retefuente], billingAccount: null);

        UseOwnBilling(quotation, account: null);
        // Ruling C9: sin empresa no hay selectores, pero una lista vacía se acepta.
        quotation.SetBillingWithholdings([], AdvisorId, Now);

        Assert.Null(quotation.BillingAccount);
        Assert.NotNull(quotation.Billing);
        Assert.Empty(quotation.BillingWithholdings);
    }

    [Fact]
    public void SelectorOfAnotherCountryIs422()
    {
        var quotation = NewQuotation(billingAccount: ColombianAccount);
        UseOwnBilling(quotation, ColombianAccount);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetBillingWithholdings([RetefuenteSpain], AdvisorId, Now));

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", error.Code);
        Assert.Empty(quotation.BillingWithholdings);
    }

    [Fact]
    public void TwoSelectorsOfTheSameTypeAre422()
    {
        var quotation = NewQuotation(billingAccount: ColombianAccount);
        UseOwnBilling(quotation, ColombianAccount);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetBillingWithholdings([Retefuente, RetefuenteServicios], AdvisorId, Now));

        Assert.Equal("quotation.billing.withholding_type_repeated", error.Code);
        Assert.Empty(quotation.BillingWithholdings);
    }

    [Fact]
    public void SelectorsWithoutCompanyAre422()
    {
        var quotation = NewQuotation(billingAccount: null);
        UseOwnBilling(quotation, account: null);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetBillingWithholdings([Retefuente], AdvisorId, Now));

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", error.Code);
    }

    [Fact]
    public void SelectorsWithoutAnOwnBillingPartyAre422()
    {
        var quotation = NewQuotation(billingAccount: ColombianAccount);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetBillingWithholdings([Retefuente], AdvisorId, Now));

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", error.Code);
    }

    [Fact]
    public void SettingTheSameSelectorsAgainChangesNothing()
    {
        var quotation = NewQuotation(billingAccount: ColombianAccount);
        UseOwnBilling(quotation, ColombianAccount);
        quotation.SetBillingWithholdings([Retefuente, ReteIca], AdvisorId, Now);
        var version = quotation.Version;

        Assert.False(quotation.SetBillingWithholdings([Retefuente, ReteIca], AdvisorId, Now));

        Assert.Equal(version, quotation.Version);
    }

    // Parte propia con el excedente contestado y sin selectores: convertible. Las retenciones no se preguntan.
    [Fact]
    public void ConvertRequiresOnlyVatSurplusAnswer()
    {
        var quotation = NewQuotation(billingAccount: ColombianAccount);
        AddLine(quotation);
        UseOwnBilling(quotation, ColombianAccount, vatSurplus: false);

        Assert.True(quotation.CanBeConvertedToOrder);
        quotation.EnsureConvertibleToOrder();

        UseOwnBilling(quotation, ColombianAccount, vatSurplus: null);

        Assert.False(quotation.CanBeConvertedToOrder);
        var error = Assert.Throws<QuotationsDomainException>(quotation.EnsureConvertibleToOrder);
        Assert.Equal("quotation.billing.tax_profile_required", error.Code);
    }

    [Fact]
    public void AmountsFollowLineEdits()
    {
        var quotation = NewQuotation([Retefuente], ColombianAccount);
        AddLine(quotation);
        Assert.Equal(2_500m, quotation.RetentionAmount);

        AddLine(quotation, unitPrice: 238_000m);

        var applied = Assert.Single(quotation.AppliedWithholdings);
        Assert.Equal(300_000m, applied.BaseAmount);
        Assert.Equal(7_500m, applied.Amount);
        Assert.Equal(7_500m, quotation.RetentionAmount);
        // La tasa congelada no cambia: sólo el monto.
        Assert.Equal(Retefuente, applied.Rate);
    }
}

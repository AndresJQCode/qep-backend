using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// IVA por empresa (spec 2026-11-17 §5.2, R1, R2): lo que emite una empresa que no cobra IVA va al 0 % en líneas y envío,
/// y el cliente paga el precio de lista. La línea guarda la tasa del producto aparte de la efectiva, y el agregado rehace
/// las efectivas cuando cambia el IVA de la empresa emisora.
/// </summary>
public sealed class CompanyVatTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static QuotationBillingAccount Company(bool chargesVat) => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "CO",
        ChargesVat = chargesVat,
    };

    private static ShippingDetails ChargedFreight(int taxPercentage = 19) =>
        new(null, null, FreightChargeMode.Charged, 11_900m, taxPercentage, false);

    private static Quotation NewQuotation(QuotationBillingAccount? account, bool customerVatSurplus = false)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, Today.AddDays(15), null, null,
            QuotationParties.Empty, account, "COP", customerVatSurplus, [], MunicipalContext.None, Advisor, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, Advisor, Now);
        return quotation;
    }

    private static void ChangeCompany(Quotation quotation, QuotationBillingAccount account, int? freightRate = null) =>
        quotation.UpdateDetails(
            quotation.ValidUntil, null, null, QuotationParties.Empty, account, repricing: null, isRetail: false,
            globalScaleFloor: null, MunicipalContext.None, Advisor, Now, freightRate);

    [Fact]
    public void ALineOfACompanyWithoutVatChargesTheListPrice()
    {
        var quotation = NewQuotation(Company(chargesVat: false));

        var line = Assert.Single(quotation.Items);
        Assert.Equal(0, line.TaxPercentage);
        Assert.Equal(0m, line.TaxAmount);
        Assert.Equal(119_000m, line.Subtotal);
        Assert.Equal(19, line.ProductTaxPercentage);
        Assert.Equal(119_000m, quotation.Total);
    }

    [Fact]
    public void FreightOfACompanyWithoutVatHasNoTax()
    {
        var quotation = NewQuotation(Company(chargesVat: false));

        quotation.SetShipping(ChargedFreight(), Advisor, Now);

        Assert.Equal(0, quotation.FreightTaxPercentage);
        Assert.Equal(0m, quotation.FreightTaxAmount);
    }

    [Fact]
    public void ChangingToACompanyWithoutVatRecalculatesTheLines()
    {
        var quotation = NewQuotation(Company(chargesVat: true));
        var totalWithVat = quotation.Total;
        Assert.True(Assert.Single(quotation.Items).TaxAmount > 0m);

        ChangeCompany(quotation, Company(chargesVat: false));

        var line = Assert.Single(quotation.Items);
        Assert.Equal(0m, line.TaxAmount);
        Assert.Equal(0m, quotation.TaxAmount);
        Assert.Equal(totalWithVat, quotation.Total);
    }

    [Fact]
    public void ChangingBackToACompanyWithVatRestoresTheProductRate()
    {
        var quotation = NewQuotation(Company(chargesVat: false));

        ChangeCompany(quotation, Company(chargesVat: true));

        var line = Assert.Single(quotation.Items);
        Assert.Equal(19, line.TaxPercentage);
        Assert.True(line.TaxAmount > 0m);
    }

    // Review Focus 2: una fila anterior a la columna no tiene tasa del producto; se lee como la efectiva que tenía.
    [Fact]
    public void ALegacyLineWithoutProductRateRecoversItsRateWhenVatReturns()
    {
        var quotation = NewQuotation(Company(chargesVat: true));
        var line = Assert.Single(quotation.Items);
        typeof(QuotationItem).GetProperty(nameof(QuotationItem.ProductTaxPercentage))!.SetValue(line, null);
        Assert.Equal(19, line.ProductTaxRate);

        ChangeCompany(quotation, Company(chargesVat: false));
        Assert.Equal(0, line.TaxPercentage);
        ChangeCompany(quotation, Company(chargesVat: true));

        Assert.Equal(19, line.TaxPercentage);
    }

    [Fact]
    public void TurningVatOnWithChargedFreightUsesTheGivenFreightRate()
    {
        var quotation = NewQuotation(Company(chargesVat: false));
        quotation.SetShipping(ChargedFreight(), Advisor, Now);
        var withVat = Company(chargesVat: true);
        Assert.True(quotation.NeedsFreightTaxRateFor(withVat));

        ChangeCompany(quotation, withVat, freightRate: 19);

        Assert.Equal(19, quotation.FreightTaxPercentage);
        Assert.True(quotation.FreightTaxAmount > 0m);
    }

    [Fact]
    public void TurningVatOnWithChargedFreightWithoutARateIsABug()
    {
        var quotation = NewQuotation(Company(chargesVat: false));
        quotation.SetShipping(ChargedFreight(), Advisor, Now);

        Assert.Throws<InvalidOperationException>(() => ChangeCompany(quotation, Company(chargesVat: true)));
    }

    // Re-enviar la misma empresa no relee su IVA: el documento conserva la foto (anti-objetivo del spec §2).
    [Fact]
    public void ResendingTheSameCompanyKeepsTheFrozenVat()
    {
        var account = Company(chargesVat: false);
        var quotation = NewQuotation(account);

        ChangeCompany(quotation, account with { ChargesVat = true });

        Assert.False(quotation.ChargesVat);
        Assert.Equal(0, Assert.Single(quotation.Items).TaxPercentage);
    }

    // §5.2: si los dos aplican gana «no cobra IVA», y el cliente paga el precio de lista.
    [Fact]
    public void VatSurplusAndNoVatTogetherChargeTheListPrice()
    {
        var quotation = NewQuotation(Company(chargesVat: false), customerVatSurplus: true);

        Assert.True(quotation.AppliesVatSurplus);
        Assert.Equal(119_000m, quotation.Total);
    }

    [Fact]
    public void AnOrderFromAQuotationWithoutVatKeepsItsLines()
    {
        var quotation = NewQuotation(Company(chargesVat: false));

        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, null, Advisor, [], Now, Today);

        Assert.False(order.ChargesVat);
        var line = Assert.Single(order.Items);
        Assert.Equal(0, line.TaxPercentage);
        Assert.Equal(19, line.ProductTaxPercentage);
        Assert.Equal(quotation.Total, order.Total);
    }

    [Fact]
    public void RepricingAnOrderWithoutVatKeepsZeroTax()
    {
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0002",
            new OrderHeader(ClientId, Advisor, "COP", null, false, null, false, []), QuotationParties.Empty,
            Company(chargesVat: false), ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, null)],
            null, Advisor, [], Now, Today);
        var line = Assert.Single(order.Items);

        order.UpdateItemQuantity(line.Id, 2m, 0m, 19, Advisor, Now);

        Assert.Equal(0, line.TaxPercentage);
        Assert.Equal(0m, line.TaxAmount);
        Assert.Equal(19, line.ProductTaxPercentage);
    }

    [Fact]
    public void AnOrderChangingToACompanyWithVatTakesTheGivenFreightRate()
    {
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0003",
            new OrderHeader(ClientId, Advisor, "COP", null, false, null, false, []), QuotationParties.Empty,
            Company(chargesVat: false), ChargedFreight(),
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, null)],
            null, Advisor, [], Now, Today);
        Assert.Equal(0, order.FreightTaxPercentage);
        var withVat = Company(chargesVat: true);
        Assert.True(order.NeedsFreightTaxRateFor(withVat));

        order.ApplyBillingContext(withVat, MunicipalContext.None, Advisor, Now, freightTaxPercentageWithVat: 19);

        Assert.Equal(19, order.FreightTaxPercentage);
        Assert.Equal(19, Assert.Single(order.Items).TaxPercentage);
    }
}

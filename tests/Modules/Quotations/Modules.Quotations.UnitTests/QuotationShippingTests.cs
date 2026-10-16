using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>La entrega y el envío de la cotización (spec 2026-10-17 flete §5.1-§5.3).</summary>
public sealed class QuotationShippingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CarrierId = Guid.CreateVersion7();

    private static QuotationBillingAccount Account(string currency) => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = currency,
    };

    private static Quotation NewQuotation(string currency = "COP", bool isStorePickup = false) =>
        Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", Guid.CreateVersion7(), Advisor, new DateOnly(2026, 11, 1),
            null, null, new QuotationParties(null, null, IsStorePickup: isStorePickup), Account(currency), currency,
            customerWithRetention: false, customerVatSurplus: false, Advisor, Now);

    // Una línea de 10 × 11.900 con IVA del 19 %: subtotal 100.000, IVA 19.000, total 119.000.
    private static Quotation QuotationWithALineOf119000()
    {
        var quotation = NewQuotation();
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 10m, 11_900m, 0m, 19, Advisor, Now);
        return quotation;
    }

    [Fact]
    public void ChargedFreightEntersTheTotal()
    {
        var quotation = QuotationWithALineOf119000();

        quotation.SetShipping(
            new ShippingDetails(CarrierId, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, false),
            Advisor, Now);

        Assert.Equal(134_000m, quotation.Total);
        Assert.Equal(12_605.04m, quotation.FreightSubtotal);
        Assert.Equal(2_394.96m, quotation.FreightTaxAmount);
        Assert.Equal(119_000m, quotation.ProductsTotal);
        Assert.Equal((CarrierId, "Coordinadora"), (quotation.CarrierId, quotation.CarrierName));
    }

    [Fact]
    public void PickupRejectsAnyShipping()
    {
        var quotation = NewQuotation(isStorePickup: true);

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.SetShipping(
            new ShippingDetails(CarrierId, "Coordinadora", null, 0m, 0, false), Advisor, Now));

        Assert.Equal("quotation.quotation.shipping_not_allowed_on_pickup", error.Code);
    }

    [Fact]
    public void PickupAcceptsNoShipping()
    {
        var quotation = NewQuotation(isStorePickup: true);

        quotation.SetShipping(ShippingDetails.None, Advisor, Now);

        Assert.Equal(ShippingDetails.None, quotation.ShippingDetails);
    }

    [Fact]
    public void ChargedWithZeroIsRejected()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.SetShipping(
            new ShippingDetails(null, null, FreightChargeMode.Charged, 0m, 19, false), Advisor, Now));

        Assert.Equal("quotation.quotation.freight_amount_required", error.Code);
    }

    [Theory]
    [InlineData(FreightChargeMode.Free)]
    [InlineData(null)]
    public void FreeWithAmountIsRejected(FreightChargeMode? mode)
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.SetShipping(
            new ShippingDetails(null, null, mode, 5_000m, 0, false), Advisor, Now));

        Assert.Equal("quotation.quotation.freight_amount_not_allowed", error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15_000.5)]
    public void NegativeOrFractionalCopAmountIsRejected(decimal amount)
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.SetShipping(
            new ShippingDetails(null, null, FreightChargeMode.Charged, amount, 19, false), Advisor, Now));

        Assert.Equal("quotation.quotation.freight_amount_invalid", error.Code);
    }

    // OnDelivery es informativo: el valor no suma al total ni lleva IVA en QEP.
    [Fact]
    public void OnDeliveryDoesNotEnterTheTotal()
    {
        var quotation = QuotationWithALineOf119000();

        quotation.SetShipping(
            new ShippingDetails(CarrierId, "Coordinadora", FreightChargeMode.OnDelivery, 15_000m, 19, false),
            Advisor, Now);

        Assert.Equal(119_000m, quotation.Total);
        Assert.Equal((0m, 0m), (quotation.FreightSubtotal, quotation.FreightTaxAmount));
        Assert.Equal(15_000m, quotation.FreightAmount);
    }

    // Decisión 11: transportadora y modo son independientes.
    [Fact]
    public void ShippingWithoutCarrierIsAccepted()
    {
        var quotation = QuotationWithALineOf119000();

        quotation.SetShipping(new ShippingDetails(null, null, FreightChargeMode.Charged, 15_000m, 19, false), Advisor, Now);

        Assert.Null(quotation.CarrierId);
        Assert.Equal(134_000m, quotation.Total);
    }

    // Decisión 12: la contraentrega tampoco exige transportadora.
    [Fact]
    public void CashOnDeliveryWithoutCarrierIsAccepted()
    {
        var quotation = NewQuotation();

        quotation.SetShipping(new ShippingDetails(null, null, null, 0m, 0, true), Advisor, Now);

        Assert.True(quotation.CashOnDelivery);
    }

    [Fact]
    public void SetShippingRequiresAnEditableQuotation()
    {
        var quotation = NewQuotation();
        quotation.Void(Advisor, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetShipping(ShippingDetails.None, Advisor, Now));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    // Review Focus 3 (D-F15): el envío no tiene de dónde re-preciarse en la moneda nueva.
    [Fact]
    public void ChangingCurrencyClearsTheFreightAmountAndMode()
    {
        var quotation = NewQuotation();
        quotation.SetShipping(
            new ShippingDetails(CarrierId, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, true),
            Advisor, Now);

        quotation.UpdateDetails(
            new DateOnly(2026, 11, 1), null, null, QuotationParties.Empty, Account("USD"), repricing: null,
            isRetail: false, globalScaleFloor: null, Advisor, Now);

        Assert.Equal("USD", quotation.Currency);
        Assert.Null(quotation.FreightChargeMode);
        Assert.Equal(0m, quotation.FreightAmount);
        Assert.Equal(0, quotation.FreightTaxPercentage);
        Assert.Equal((0m, 0m), (quotation.FreightSubtotal, quotation.FreightTaxAmount));
        Assert.Equal((CarrierId, "Coordinadora", true), (quotation.CarrierId, quotation.CarrierName, quotation.CashOnDelivery));
    }

    [Fact]
    public void KeepingTheCurrencyKeepsTheFreight()
    {
        var quotation = NewQuotation();
        quotation.SetShipping(new ShippingDetails(null, null, FreightChargeMode.Charged, 15_000m, 19, false), Advisor, Now);

        quotation.UpdateDetails(
            new DateOnly(2026, 11, 1), null, null, QuotationParties.Empty, Account("COP"), repricing: null,
            isRetail: false, globalScaleFloor: null, Advisor, Now);

        Assert.Equal((FreightChargeMode.Charged, 15_000m, 19), (quotation.FreightChargeMode, quotation.FreightAmount, quotation.FreightTaxPercentage));
    }

    // Fuera de COP no se discrimina el IVA: la tasa se guarda en 0 y el valor entra tal cual.
    [Fact]
    public void UsdChargedFreightHasNoTax()
    {
        var quotation = NewQuotation(currency: "USD");

        quotation.SetShipping(new ShippingDetails(null, null, FreightChargeMode.Charged, 50.5m, 19, false), Advisor, Now);

        Assert.Equal(0, quotation.FreightTaxPercentage);
        Assert.Equal((50.5m, 0m), (quotation.FreightSubtotal, quotation.FreightTaxAmount));
        Assert.Equal(50.5m, quotation.Total);
    }

    [Fact]
    public void ShippingDetailsReflectsTheStoredFields()
    {
        var quotation = NewQuotation();
        var shipping = new ShippingDetails(CarrierId, "Coordinadora", FreightChargeMode.Free, 0m, 0, true);

        quotation.SetShipping(shipping, Advisor, Now);

        Assert.Equal(shipping, quotation.ShippingDetails);
    }
}
